using System.Globalization;
using System.Net;
using System.Text;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Osm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Simplify;

namespace Gorodki.OsmPipeline;

/// <summary>
/// Карта-артефакт для Никиты (osm-pipeline.md, «Проверка», «Живые данные»; вопросы 1.2, 3, 6.6): Арена и кварталы,
/// маски, «достижимое», кандидаты — корпуса и общежития БрГТУ, мемориалы, военные объекты, «магистрали» и тротуары.
/// Самодостаточный HTML: без тайлов карт и внешних картинок, контуры — SVG из самих данных OSM.
/// </summary>
/// <remarks>Упрощение контуров — только для показа (§7.3); набор и маски захвата не упрощаются.</remarks>
public static class Preview
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Html(OsmSetFileContent set, OsmInput input, PipelineParams parameters)
    {
        // Числа на странице — по-русски (запятая, пробел между разрядами); координаты SVG — всегда инвариантно (класс Svg).
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = Ru;
        try
        {
            return Build(set, input, parameters);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static string Build(OsmSetFileContent set, OsmInput input, PipelineParams parameters)
    {
        var data = set.Data;
        var city = data.Districts.Single(d => d.Kind == DistrictKind.City);
        var arena = data.Districts.SingleOrDefault(d => d.Kind == DistrictKind.Arena);
        var quarters = data.Districts.Where(d => d.Kind == DistrictKind.Quarter).ToList();
        var anchors = new HashSet<string>(parameters.Arena?.Anchors ?? [], StringComparer.Ordinal);
        var campus = anchors.Count > 0
            ? GeoOps.UnionAll(input.Features.Where(f => anchors.Contains(f.Ref) && f.IsArea).Select(f => f.Geometry)).Centroid.Coordinate
            : city.Geometry.Centroid.Coordinate;
        var cellArea = FogTileCodec.CellAreaSquareMeters(data.Reachable.Keys.First());
        var sharedBorder = input.BorderLine is { } stateLine ? GeoOps.LengthInside(input.City.Boundary, GeoOps.Buffer(stateLine, 1)) : 0;

        var html = new StringBuilder();
        html.Append(Head);
        html.Append("<main>");
        html.Append($"""
            <header class="top">
              <p class="eyebrow">Конвейер OSM v1 · набор {set.Version} · выгрузка Geofabrik от {set.Source.ReplicationTimestamp?.ToString("dd.MM.yyyy", Ru) ?? "?"}</p>
              <h1>Арена БрГТУ и маски Бреста</h1>
              <p class="lede">Предложение Claude по данным OpenStreetMap — <strong>ничего из этого ещё не утверждено</strong>.
              Ниже: граница Арены и 6–8 кварталов по улицам, маски захвата, «достижимая» площадь и списки кандидатов с id объектов
              OSM. Отметь, что верно, что поправить и чего в OSM нет.</p>
            </header>
            """);

        // ── Что решить ──────────────────────────────────────────────────────
        html.Append($$"""
            <section class="asks" aria-labelledby="asks-h">
              <h2 id="asks-h">Что решить Никите</h2>
              <ol class="asklist">
                <li><b>Корпуса и общежития Арены</b> — таблица «Кандидаты БрГТУ»: отметь корпуса и общежития, допиши то, чего в OSM нет. Корпус 7 (ул. Дзержинского) далеко от кампуса — брать ли его в Арену?</li>
                <li><b>Разрез на кварталы и названия</b> — карта Арены. Кварталы неравные: «Заводской» и «Мухавец» заметно больше остальных. Можно резать иначе — назови улицы.</li>
                <li><b>Мемориалы</b> — таблица «Мемориалы-кандидаты»: какие закрыть от захвата (площадной — по контуру, точечный — круг 50 м). Пока список пуст, слоя нет.</li>
                <li><b>«Магистрали» и тротуары</b> — таблица «Trunk и motorway». Московская, пр. Республики, Октябрьской Революции, Варшавское шоссе в OSM — trunk и в основном без тега тротуара, поэтому почти целиком в маске 12 м. Где тротуар есть — назови улицу, её пути уйдут в исключения.</li>
                <li><b>Погранполоса</b> — {{(sharedBorder > 0 ? $"граница города доходит до госграницы: ≈{sharedBorder / 1000:F1} км общей линии (по Бугу)." : "граница города до госграницы не доходит.")}} Ширины полосы из официальных правил нет, поэтому слоя нет. По решению 6.4 весь Брест открыт для захвата, только если к 04.11 появится ширина или твоя временная линия у Буга; иначе в Сезоне 0 — только Арена.</li>
                <li><b>Военные объекты</b> — таблица «Военные объекты из OSM»: знаешь ли места, которых там нет.</li>
              </ol>
            </section>
            """);

        // ── Числа ────────────────────────────────────────────────────────────
        html.Append("<section aria-labelledby=\"num-h\"><h2 id=\"num-h\">Набор в цифрах</h2><div class=\"stats\">");
        Stat(html, "Площадь города", $"{city.Geometry.Area / 1e6:F1} км²", $"без масок {city.AreaWithoutMasks / 1e6:F1} км²");
        Stat(html, "«Достижимое»", $"{city.CellCount * cellArea / 1e6:F1} км²", $"{city.CellCount:N0} клеток тумана — знаменатель «% Бреста»");
        if (arena is not null)
        {
            Stat(html, "Арена", $"{arena.Geometry.Area / 1e6:F2} км²", $"без масок {arena.AreaWithoutMasks / 1e6:F2} км², {quarters.Count} кварталов");
        }

        foreach (var district in data.Districts.Where(d => d.Kind == DistrictKind.District))
        {
            Stat(html, district.Name, $"{district.Geometry.Area / 1e6:F1} км²", $"«достижимое» {district.CellCount * cellArea / 1e6:F1} км²");
        }

        html.Append("</div>");
        html.Append("<div class=\"scroll\"><table><thead><tr><th>Маска</th><th class=\"n\">Кусков</th><th class=\"n\">Вершин</th><th class=\"n\">Площадь, га</th><th>Что это</th></tr></thead><tbody>");
        foreach (var group in data.Masks.GroupBy(m => m.Kind).OrderBy(g => g.Key))
        {
            html.Append($"<tr><td><span class=\"key k-{OsmCodes.Of(group.Key)}\"></span>{MaskName(group.Key)}</td><td class=\"n\">{group.Count():N0}</td><td class=\"n\">{group.Sum(m => m.Geometry.NumPoints):N0}</td><td class=\"n\">{group.Sum(m => m.Geometry.Area) / 1e4:N1}</td><td>{MaskRule(group.Key, parameters)}</td></tr>");
        }

        html.Append("</tbody></table></div>");
        html.Append("<ul class=\"notes\">");
        foreach (var note in data.Notes)
        {
            html.Append($"<li>{E(note)}</li>");
        }

        html.Append("</ul></section>");

        // ── Карта Арены ─────────────────────────────────────────────────────
        if (arena is not null)
        {
            html.Append(ArenaMap(data, input, arena, quarters, anchors, parameters));
            html.Append("<section aria-labelledby=\"q-h\"><h2 id=\"q-h\">Кварталы (предложение)</h2><div class=\"scroll\"><table><thead><tr><th>Квартал</th><th class=\"n\">Площадь, км²</th><th class=\"n\">Без масок, км²</th><th class=\"n\">«Достижимое», км²</th><th class=\"n\">Доля Арены без масок</th></tr></thead><tbody>");
            for (var i = 0; i < quarters.Count; i++)
            {
                var q = quarters[i];
                html.Append($"<tr><td><span class=\"key q{i % 8}\"></span>{E(q.Name)}</td><td class=\"n\">{q.Geometry.Area / 1e6:F2}</td><td class=\"n\">{q.AreaWithoutMasks / 1e6:F2}</td><td class=\"n\">{q.CellCount * cellArea / 1e6:F2}</td><td class=\"n\">{q.AreaWithoutMasks / arena.AreaWithoutMasks:P0}</td></tr>");
            }

            html.Append($"</tbody></table></div><p class=\"small\">Улицы разреза: {E(string.Join(", ", parameters.Arena!.CutStreets))}; перемычки без имени — пути {E(string.Join(", ", parameters.Arena.CutWayIds.Select(id => $"way/{id}")))}. Правило: грань сети улиц входит в Арену, если ≥{parameters.Arena.MinShareInside:P0} её площади — не дальше {parameters.Arena.RadiusMeters:N0} м от корпусов и общежитий; грань без метки квартала присоединяется к соседу с самой длинной общей границей.</p></section>");
        }

        html.Append(Candidates(input, anchors, campus));
        html.Append(CityMap(data, input, arena, parameters));
        html.Append(Trunks(input, parameters));
        html.Append(Memorials(input));
        html.Append(Military(input));

        html.Append($"""
            <footer>
              <p><b>© участники OpenStreetMap</b> (OpenStreetMap contributors) — данные под лицензией Open Database License (ODbL) 1.0:
              openstreetmap.org/copyright. Набор {set.Version} — производная база данных под ODbL; отпечаток <code>{set.Fingerprint[..16]}…</code>,
              выгрузка <code>{E(set.Source.File)}</code> (SHA-256 <code>{set.Source.Sha256[..16]}…</code>), {E(string.Join("; ", set.Source.Tools.Select(t => $"{t.Key} {t.Value}")))}.</p>
              <p>Собрано командой <code>Gorodki.OsmPipeline preview</code> из набора и тем osmium; схема — docs/architecture/osm-pipeline.md.</p>
            </footer>
            """);
        html.Append("</main>");
        return html.ToString();
    }

    // ── Карта Арены ─────────────────────────────────────────────────────────

    private static string ArenaMap(OsmSetData data, OsmInput input, DistrictData arena, IReadOnlyList<DistrictData> quarters, HashSet<string> anchors, PipelineParams parameters)
    {
        var view = new Envelope(arena.Geometry.EnvelopeInternal);
        view.ExpandBy(350);
        var svg = new Svg(view, 1);

        svg.Open("g", "class=\"reach\"");
        svg.Raw($"<path d=\"{ReachablePath(data.Reachable, view, svg)}\"/>");
        svg.Close("g");

        foreach (var group in data.Masks.Where(m => m.Kind != MaskKind.Outside && m.Geometry.EnvelopeInternal.Intersects(view)).GroupBy(m => m.Kind))
        {
            svg.Path(GeoOps.UnionAll(group.Select(m => (Geometry)m.Geometry)), $"mask k-{OsmCodes.Of(group.Key)}", simplify: 0.5);
        }

        var buildings = input.Features.Where(f => f.IsArea && f.Tag("building") is not null && f.Geometry.EnvelopeInternal.Intersects(view)).ToList();
        svg.Path(GeoOps.Factory.BuildGeometry(buildings.Where(b => !anchors.Contains(b.Ref)).Select(b => b.Geometry).ToList()), "bld", simplify: 0.8);

        foreach (var street in input.Features.Where(f => f.IsLine && f.Tag("highway") is { } hw && StreetClass(hw) is not null && f.Geometry.EnvelopeInternal.Intersects(view)))
        {
            svg.Line(street.Geometry, $"st {StreetClass(street.Tag("highway")!)}", simplify: 0.8);
        }

        for (var i = 0; i < quarters.Count; i++)
        {
            svg.Path(quarters[i].Geometry, $"quarter q{i % 8}", simplify: 1);
        }

        svg.Path(arena.Geometry, "arena", simplify: 1);
        var anchorShapes = input.Features.Where(f => anchors.Contains(f.Ref) && f.IsArea).Select(f => f.Geometry).ToList();
        if (anchorShapes.Count > 0)
        {
            var zone = GeoOps.Buffer(GeoOps.UnionAll(anchorShapes), parameters.Arena!.RadiusMeters);
            svg.Line(zone.Boundary, "radius", simplify: 2);
            svg.Path(GeoOps.Factory.BuildGeometry(anchorShapes), "anchor", simplify: 0);
        }

        for (var i = 0; i < quarters.Count; i++)
        {
            var point = GeoOps.InteriorPoint(quarters[i].Geometry);
            svg.Label(point, quarters[i].Name, "qlabel");
        }

        return Figure(
            "arena-h",
            "Арена и кварталы — предложение",
            svg,
            """
            <span><i class="key arena-key"></i>граница Арены</span>
            <span><i class="key radius-key"></i>1,5 км от корпусов и общежитий (ориентир)</span>
            <span><i class="key anchor-key"></i>корпуса и общежития в рецепте</span>
            <span><i class="key reach-key"></i>«достижимое» — клетки тумана</span>
            <span><i class="key k-water"></i>вода</span><span><i class="key k-rail"></i>ж/д</span>
            <span><i class="key k-major_road"></i>«магистрали»</span><span><i class="key k-military"></i>военные</span>
            <span><i class="key k-cemetery"></i>кладбища</span>
            """,
            "Цветом кварталов — предложение разреза; толстая линия — граница Арены. Серые контуры — здания из OSM, линии — улицы.");
    }

    /// <summary>«Достижимые» клетки — горизонтальными отрезками строк сетки тумана, одним путём SVG.</summary>
    private static string ReachablePath(SortedDictionary<FogTileKey, FogTileBits> reachable, Envelope view, Svg svg)
    {
        var path = new StringBuilder();
        foreach (var (key, bits) in reachable)
        {
            if (!ReachableRaster.UtmEnvelope(key).Intersects(view))
            {
                continue;
            }

            for (var row = 0; row < 256; row++)
            {
                var col = 0;
                while (col < 256)
                {
                    if (!bits.IsSet((row * 256) + col))
                    {
                        col++;
                        continue;
                    }

                    var start = col;
                    while (col < 256 && bits.IsSet((row * 256) + col))
                    {
                        col++;
                    }

                    var x0 = (key.X << 8) + start;
                    var x1 = (key.X << 8) + col;
                    var y0 = (key.Y << 8) + row;
                    var corners = new[] { Corner(x0, y0), Corner(x1, y0), Corner(x1, y0 + 1), Corner(x0, y0 + 1) };
                    if (!corners.Any(c => view.Contains(c)))
                    {
                        continue;
                    }

                    path.Append('M').Append(svg.Point(corners[0]));
                    path.Append('L').Append(svg.Point(corners[1]));
                    path.Append('L').Append(svg.Point(corners[2]));
                    path.Append('L').Append(svg.Point(corners[3])).Append('Z');
                }
            }
        }

        return path.ToString();
    }

    /// <summary>Угол клетки G22 (пиксель веб-меркатора уровня 22) в UTM.</summary>
    private static Coordinate Corner(int x, int y)
    {
        const double World = 1 << FogGrid.Zoom;
        var longitude = (x / World * 360) - 180;
        var latitude = Math.Atan(Math.Sinh(Math.PI * (1 - (2 * y / World)))) * 180 / Math.PI;
        var (easting, northing) = Utm34.Forward(latitude, longitude);
        return new Coordinate(easting, northing);
    }

    private static string? StreetClass(string highway) => highway switch
    {
        "motorway" or "motorway_link" or "trunk" or "trunk_link" or "primary" or "primary_link" => "big",
        "secondary" or "secondary_link" or "tertiary" or "tertiary_link" => "mid",
        "residential" or "living_street" or "unclassified" or "service" or "pedestrian" => "small",
        "footway" or "path" or "steps" or "cycleway" or "track" => "foot",
        _ => null,
    };

    // ── Карта города ────────────────────────────────────────────────────────

    private static string CityMap(OsmSetData data, OsmInput input, DistrictData? arena, PipelineParams parameters)
    {
        var view = new Envelope(input.City.EnvelopeInternal);
        view.ExpandBy(400);
        var svg = new Svg(view, 5);
        var themes = new Themes(parameters);
        foreach (var group in data.Masks.Where(m => m.Kind != MaskKind.Outside).GroupBy(m => m.Kind))
        {
            svg.Path(GeoOps.UnionAll(group.Select(m => (Geometry)m.Geometry)), $"mask k-{OsmCodes.Of(group.Key)}", simplify: 4);
        }

        foreach (var street in input.Features.Where(f => f.IsLine && f.Tag("highway") is { } hw && StreetClass(hw) == "big"))
        {
            svg.Line(street.Geometry, themes.IsMajorRoad(street) ? "st trunk-mask" : "st big", simplify: 4);
        }

        foreach (var district in data.Districts.Where(d => d.Kind == DistrictKind.District))
        {
            svg.Line(district.Geometry.Boundary, "district", simplify: 4);
            svg.Label(GeoOps.InteriorPoint(district.Geometry), district.Name, "dlabel");
        }

        svg.Line(input.City.Boundary, "cityline", simplify: 4);
        if (input.BorderLine is { } border)
        {
            svg.Line(GeoOps.LineInside(border, GeoOps.Factory.ToGeometry(view)), "stateline", simplify: 4);
        }

        if (arena is not null)
        {
            svg.Path(arena.Geometry, "arena", simplify: 4);
        }

        return Figure(
            "city-h",
            "Маски по всему Бресту",
            svg,
            """
            <span><i class="key k-water"></i>вода</span><span><i class="key k-rail"></i>ж/д</span>
            <span><i class="key k-major_road"></i>«магистрали» (маска 12 м)</span><span><i class="key k-military"></i>военные</span>
            <span><i class="key k-cemetery"></i>кладбища</span><span><i class="key trunkmask-key"></i>trunk в маске</span>
            <span><i class="key big-key"></i>trunk с тротуаром и главные улицы</span>
            <span><i class="key cityline-key"></i>граница города</span><span><i class="key stateline-key"></i>госграница</span>
            <span><i class="key arena-key"></i>Арена</span>
            """,
            "Маски обрезаны границей города. Погранполосы нет: ширины из официальных правил нет. Пунктир — граница Ленинского и Московского районов.");
    }

    // ── Таблицы кандидатов ──────────────────────────────────────────────────

    private static string Candidates(OsmInput input, HashSet<string> anchors, Coordinate campus)
    {
        static bool IsBrstu(OsmFeature f) =>
            f.Tags.Where(kv => kv.Key.StartsWith("name", StringComparison.Ordinal) || kv.Key.StartsWith("operator", StringComparison.Ordinal) || kv.Key.StartsWith("official_name", StringComparison.Ordinal) || kv.Key == "short_name")
                .Any(kv => kv.Value.Contains("БрГТУ", StringComparison.Ordinal) || kv.Value.Contains("БрДТУ", StringComparison.Ordinal)
                    || kv.Value.Contains("тэхнічны ўніверсітэт", StringComparison.OrdinalIgnoreCase) || kv.Value.Contains("технический университет", StringComparison.OrdinalIgnoreCase));

        var rows = input.Features
            .Where(f => f.IsArea && (f.Tag("building") is not null || f.Tag("amenity") == "university"))
            .Where(f => anchors.Contains(f.Ref) || IsBrstu(f) || f.Tag("amenity") == "university"
                || (f.Tag("building") is "university" or "dormitory" && f.Geometry.Centroid.Coordinate.Distance(campus) < 1_500))
            .GroupBy(f => f.Ref).Select(g => g.First())
            .OrderByDescending(f => anchors.Contains(f.Ref)).ThenBy(f => f.Geometry.Centroid.Coordinate.Distance(campus))
            .ToList();
        var html = new StringBuilder("""
            <section aria-labelledby="c-h"><h2 id="c-h">Кандидаты БрГТУ — корпуса и общежития</h2>
            <p class="small">Из OSM: здания с «БрГТУ» или «технический университет» в названии или операторе, <code>amenity=university</code>
            и все <code>building=university|dormitory</code> в 1,5 км от кампуса (многие общежития — ЖРЭУ и предприятий, не БрГТУ).
            «В рецепте» — здания, вокруг которых сейчас строится Арена. Адрес ул. Московская, 267 совпал с OSM.</p>
            <div class="scroll"><table><thead><tr><th>В рецепте</th><th>Объект OSM</th><th>Название</th><th>Тип</th><th>Адрес</th><th>Оператор</th><th class="n">От кампуса, м</th></tr></thead><tbody>
            """);
        foreach (var f in rows)
        {
            var address = string.Join(", ", new[] { f.Tag("addr:street"), f.Tag("addr:housenumber") }.OfType<string>());
            html.Append($"<tr><td>{(anchors.Contains(f.Ref) ? "<span class=\"pill yes\">да</span>" : "<span class=\"pill\">нет</span>")}</td><td><code>{f.Ref}</code></td><td>{E(f.Tag("name:ru") ?? f.Tag("name") ?? "—")}</td><td>{E(f.Tag("building") ?? f.Tag("amenity") ?? "")}</td><td>{E(address)}</td><td>{E(f.Tag("operator") ?? "")}</td><td class=\"n\">{f.Geometry.Centroid.Coordinate.Distance(campus):N0}</td></tr>");
        }

        html.Append("</tbody></table></div></section>");
        return html.ToString();
    }

    private static string Trunks(OsmInput input, PipelineParams parameters)
    {
        var themes = new Themes(parameters);
        var footways = new STRtree<Geometry>();
        foreach (var f in input.Features.Where(f => f.IsLine && f.Tag("highway") is "footway" or "path" or "pedestrian" or "cycleway"))
        {
            footways.Insert(f.Geometry.EnvelopeInternal, f.Geometry);
        }

        footways.Build();
        var trunks = input.Features
            .Where(f => f.IsLine && f.Tag("highway") is "motorway" or "motorway_link" or "trunk" or "trunk_link" && f.Geometry.Intersects(input.City))
            .Select(f =>
            {
                var near = GeoOps.Buffer(f.Geometry, 20);
                var sidewalks = footways.Query(near.EnvelopeInternal).Sum(w => GeoOps.LengthInside(w, near));
                return (Feature: f, Masked: themes.IsMajorRoad(f), Length: GeoOps.LengthInside(f.Geometry, input.City), Sidewalks: sidewalks);
            })
            .GroupBy(t => (Name: t.Feature.Tag("name:ru") ?? t.Feature.Tag("name") ?? "без имени", Ref: t.Feature.Tag("ref") ?? "", t.Masked))
            .OrderByDescending(g => g.Sum(t => t.Length))
            .ToList();
        var html = new StringBuilder("""
            <section aria-labelledby="t-h"><h2 id="t-h">Trunk и motorway в городе — маска «магистралей»</h2>
            <p class="small">Решение 1.2 (А): <code>motorway</code> — всегда маска; <code>trunk</code> — маска, если нет тега тротуара
            (<code>sidewalk</code> нет или no/none) и нет <code>foot=yes|designated</code>. «Нарисованные тротуары рядом» — длина пешеходных
            линий OSM в 20 м от оси: если она сравнима с двойной длиной дороги, тротуары есть, просто нарисованы отдельно, — такую улицу стоит
            внести в исключения (<code>majorRoads.sidewalkWayIds</code>).</p>
            <div class="scroll"><table><thead><tr><th>Улица</th><th>Номер</th><th>Сейчас</th><th class="n">Длина в городе, км</th><th class="n">Тротуары рядом, км</th><th>Пути OSM</th></tr></thead><tbody>
            """);
        foreach (var g in trunks)
        {
            var ids = string.Join(" ", g.Select(t => t.Feature.Id).Order());
            html.Append($"<tr><td>{E(g.Key.Name)}</td><td>{E(g.Key.Ref)}</td><td>{(g.Key.Masked ? "<span class=\"pill warn\">маска 12 м</span>" : "<span class=\"pill yes\">пешеходный путь</span>")}</td><td class=\"n\">{g.Sum(t => t.Length) / 1000:F2}</td><td class=\"n\">{g.Sum(t => t.Sidewalks) / 1000:F2}</td><td><details><summary>{g.Count()} путей</summary><code class=\"ids\">{ids}</code></details></td></tr>");
        }

        html.Append("</tbody></table></div></section>");
        return html.ToString();
    }

    private static string Memorials(OsmInput input)
    {
        var candidates = input.Features
            .Where(f => (f.Tag("historic") is "memorial" or "monument" or "fort" || f.Tag("memorial") is not null) && (f.IsArea || f.Geometry is Point))
            .Where(f => input.City.Intersects(f.Geometry))
            .GroupBy(f => f.Ref).Select(g => g.OrderByDescending(f => f.IsArea).First())
            .ToList();
        var skipped = candidates.Count(f => !f.IsArea && f.Tag("memorial") is "plaque" or "stolperstein");
        var rows = candidates.Where(f => f.IsArea || f.Tag("memorial") is not ("plaque" or "stolperstein"))
            .OrderByDescending(f => f.IsArea).ThenByDescending(f => f.Geometry.Area).ThenBy(f => f.Tag("name") ?? "я", StringComparer.Ordinal)
            .ToList();
        var html = new StringBuilder($"""
            <section aria-labelledby="m-h"><h2 id="m-h">Мемориалы-кандидаты</h2>
            <p class="small">OSM в границах города: <code>historic=memorial|monument|fort</code> и <code>memorial=*</code> — {rows.Count} объектов
            (мемориальные доски, {skipped} шт., не показаны). Решение 6.6: площадной — по контуру, точечный — круг 50 м. Мемориал закрыт для
            захвата, но прогулка по нему прибавляет «% Бреста» (6.2, Б). Впиши выбранные в <code>memorials.objects</code>.</p>
            <div class="scroll"><table><thead><tr><th>Объект OSM</th><th>Название</th><th>Вид</th><th>Форма</th><th class="n">Площадь, га</th></tr></thead><tbody>
            """);
        foreach (var f in rows)
        {
            html.Append($"<tr><td><code>{f.Ref}</code></td><td>{E(f.Tag("name:ru") ?? f.Tag("name") ?? "—")}</td><td>{E(string.Join(" ", new[] { f.Tag("historic"), f.Tag("memorial") }.OfType<string>()))}</td><td>{(f.IsArea ? "площадь" : "точка → круг 50 м")}</td><td class=\"n\">{(f.IsArea ? (f.Geometry.Area / 1e4).ToString("N2", CultureInfo.CurrentCulture) : "")}</td></tr>");
        }

        html.Append("</tbody></table></div></section>");
        return html.ToString();
    }

    private static string Military(OsmInput input)
    {
        var rows = input.Features
            .Where(f => f.IsArea && (f.Tag("landuse") == "military" || f.Tag("military") is not null) && input.City.Intersects(f.Geometry))
            .OrderByDescending(f => f.Geometry.Area)
            .ToList();
        var html = new StringBuilder($"""
            <section aria-labelledby="w-h"><h2 id="w-h">Военные объекты из OSM</h2>
            <p class="small">{rows.Count} площадей <code>landuse=military</code> или <code>military=*</code>, задевающих город, — все в маске.
            Насколько полно они отмечены в OSM, неизвестно; ручные добавления — наши данные, файл в репозитории (решение 6.6).</p>
            <div class="scroll"><table><thead><tr><th>Объект OSM</th><th>Название</th><th>Теги</th><th class="n">Площадь, га</th></tr></thead><tbody>
            """);
        foreach (var f in rows)
        {
            html.Append($"<tr><td><code>{f.Ref}</code></td><td>{E(f.Tag("name:ru") ?? f.Tag("name") ?? "—")}</td><td>{E(string.Join(" ", new[] { f.Tag("landuse") is { } l ? $"landuse={l}" : null, f.Tag("military") is { } m ? $"military={m}" : null }.OfType<string>()))}</td><td class=\"n\">{f.Geometry.Area / 1e4:N1}</td></tr>");
        }

        html.Append("</tbody></table></div></section>");
        return html.ToString();
    }

    // ── Мелочи ──────────────────────────────────────────────────────────────

    private static void Stat(StringBuilder html, string label, string value, string note) =>
        html.Append($"<div class=\"stat\"><span class=\"label\">{E(label)}</span><span class=\"value\">{E(value)}</span><span class=\"small\">{E(note)}</span></div>");

    private static string MaskName(MaskKind kind) => kind switch
    {
        MaskKind.Water => "Вода",
        MaskKind.Rail => "Ж/д",
        MaskKind.MajorRoad => "«Магистрали»",
        MaskKind.Military => "Военные объекты",
        MaskKind.Cemetery => "Кладбища",
        MaskKind.Border => "Погранполоса",
        MaskKind.Memorial => "Мемориалы",
        MaskKind.Outside => "Вне игрового поля",
        _ => kind.ToString(),
    };

    private static string MaskRule(MaskKind kind, PipelineParams p) => kind switch
    {
        MaskKind.Water => "площади воды как есть",
        MaskKind.Rail => $"пути ±{p.Rail.HalfWidthMeters:N0} м и площади станций",
        MaskKind.MajorRoad => $"motorway и trunk без тротуара, ±{p.MajorRoads.HalfWidthMeters:N0} м",
        MaskKind.Military => "landuse=military, military=*",
        MaskKind.Cemetery => "landuse=cemetery, amenity=grave_yard",
        MaskKind.Border => "полоса вдоль госграницы",
        MaskKind.Memorial => "по списку Никиты",
        MaskKind.Outside => "рамка минус зона игры",
        _ => "",
    };

    private static string Figure(string id, string title, Svg svg, string legend, string caption) =>
        $"""
        <section aria-labelledby="{id}"><h2 id="{id}">{title}</h2>
        <figure class="map"><div class="mapbox">{svg}</div>
        <figcaption><div class="legend">{legend}</div><p class="small">{caption} © участники OpenStreetMap, ODbL.</p></figcaption></figure></section>
        """;

    private static string E(string text) => WebUtility.HtmlEncode(text);

    /// <summary>SVG в метрах UTM: начало — левый верхний угол рамки, ось y вниз.</summary>
    private sealed class Svg(Envelope view, int precision)
    {
        private readonly StringBuilder _body = new();

        public string Point(Coordinate c) =>
            string.Create(CultureInfo.InvariantCulture, $"{Math.Round((c.X - view.MinX) / precision) * precision:0.#} {Math.Round((view.MaxY - c.Y) / precision) * precision:0.#}");

        public void Open(string tag, string attributes) => _body.Append(CultureInfo.InvariantCulture, $"<{tag} {attributes}>");

        public void Close(string tag) => _body.Append(CultureInfo.InvariantCulture, $"</{tag}>");

        public void Raw(string text) => _body.Append(text);

        public void Path(Geometry geometry, string cssClass, double simplify)
        {
            if (geometry.IsEmpty)
            {
                return;
            }

            var shown = simplify > 0 ? DouglasPeuckerSimplifier.Simplify(geometry, simplify) : geometry; // только для показа
            var d = new StringBuilder();
            foreach (var polygon in GeoOps.Polygons(shown))
            {
                Ring(d, polygon.ExteriorRing.Coordinates);
                foreach (var hole in polygon.InteriorRings)
                {
                    Ring(d, hole.Coordinates);
                }
            }

            _body.Append(CultureInfo.InvariantCulture, $"<path class=\"{cssClass}\" d=\"{d}\"/>");
        }

        public void Line(Geometry geometry, string cssClass, double simplify)
        {
            if (geometry.IsEmpty)
            {
                return;
            }

            var shown = simplify > 0 ? DouglasPeuckerSimplifier.Simplify(geometry, simplify) : geometry;
            var d = new StringBuilder();
            for (var i = 0; i < shown.NumGeometries; i++)
            {
                var coordinates = shown.GetGeometryN(i).Coordinates;
                for (var j = 0; j < coordinates.Length; j++)
                {
                    d.Append(j == 0 ? 'M' : 'L').Append(Point(coordinates[j]));
                }
            }

            _body.Append(CultureInfo.InvariantCulture, $"<path class=\"{cssClass}\" d=\"{d}\"/>");
        }

        public void Label(Coordinate at, string text, string cssClass)
        {
            var xy = Point(at).Split(' ');
            _body.Append(CultureInfo.InvariantCulture, $"<text class=\"{cssClass}\" x=\"{xy[0]}\" y=\"{xy[1]}\" text-anchor=\"middle\">{E(text)}</text>");
        }

        private void Ring(StringBuilder d, Coordinate[] coordinates)
        {
            for (var i = 0; i < coordinates.Length - 1; i++)
            {
                d.Append(i == 0 ? 'M' : 'L').Append(Point(coordinates[i]));
            }

            d.Append('Z');
        }

        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"<svg viewBox=\"0 0 {view.Width:0} {view.Height:0}\" role=\"img\" preserveAspectRatio=\"xMidYMid meet\">{_body}</svg>");
    }

    private const string Head = """
        <meta charset="utf-8">
        <title>Кандидаты Арены БрГТУ</title>
        <style>
        :root {
          --ground: #f3f5f1; --paper: #ffffff; --ink: #1c2420; --muted: #5a6760; --rule: #d6ddd7;
          --accent: #0e6b62; --accent-soft: #d3ebe6; --warn: #a8471b; --warn-soft: #f6e0d4;
          --water: #6ea8d0; --rail: #7b6552; --major: #c2412d; --military: #7d8a45; --cemetery: #9a84c4; --border: #d02b7a;
          --reach: #58b08a; --bld: #c9cfc9; --street: #9aa39d; --street-big: #58625c;
          --q0: #e3b85a; --q1: #6fa7c9; --q2: #d98a6b; --q3: #8cbf7a; --q4: #b48fd0; --q5: #cf6f8f; --q6: #6fc4b8; --q7: #b3a068;
          --font: "Segoe UI", "Helvetica Neue", Roboto, "Noto Sans", Arial, sans-serif;
          --mono: ui-monospace, "Cascadia Mono", "SFMono-Regular", Consolas, "Liberation Mono", monospace;
        }
        @media (prefers-color-scheme: dark) {
          :root:not([data-theme="light"]) {
            color-scheme: dark;
            --ground: #111614; --paper: #18201c; --ink: #e2e9e4; --muted: #9aa8a0; --rule: #2c3631;
            --accent: #5fc7b8; --accent-soft: #173c37; --warn: #f09a6f; --warn-soft: #3d2419;
            --water: #3d79a3; --rail: #a88c74; --major: #e0634d; --military: #9bab5c; --cemetery: #a992d8; --border: #ef5aa0;
            --reach: #3e9e76; --bld: #39433e; --street: #5c6862; --street-big: #a9b4ad;
          }
        }
        :root[data-theme="dark"] {
          color-scheme: dark;
          --ground: #111614; --paper: #18201c; --ink: #e2e9e4; --muted: #9aa8a0; --rule: #2c3631;
          --accent: #5fc7b8; --accent-soft: #173c37; --warn: #f09a6f; --warn-soft: #3d2419;
          --water: #3d79a3; --rail: #a88c74; --major: #e0634d; --military: #9bab5c; --cemetery: #a992d8; --border: #ef5aa0;
          --reach: #3e9e76; --bld: #39433e; --street: #5c6862; --street-big: #a9b4ad;
        }
        body { background: var(--ground); color: var(--ink); font: 15px/1.55 var(--font); margin: 0; }
        main { max-width: 1120px; margin: 0 auto; padding-inline: 16px; padding-block: 24px 48px; display: grid; grid-template-columns: minmax(0, 1fr); gap: 36px; }
        main > * { min-width: 0; }
        h1 { font-size: clamp(1.6rem, 4vw, 2.3rem); line-height: 1.15; margin: 4px 0 10px; text-wrap: balance; letter-spacing: -0.01em; }
        h2 { font-size: 1.25rem; margin: 0 0 12px; text-wrap: balance; }
        .eyebrow { margin: 0; font-size: .78rem; letter-spacing: .06em; text-transform: uppercase; color: var(--accent); font-weight: 600; }
        .lede { max-width: 68ch; margin: 0; color: var(--ink); }
        .small { font-size: .86rem; color: var(--muted); max-width: 75ch; margin: 8px 0 0; }
        code { font-family: var(--mono); font-size: .84em; }
        .asks { background: var(--accent-soft); border-radius: 10px; padding: 18px 20px; }
        .asklist { margin: 0; padding-left: 1.3em; display: grid; gap: 8px; max-width: 80ch; }
        .stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 12px; margin-bottom: 14px; }
        .stat { background: var(--paper); border: 1px solid var(--rule); border-radius: 8px; padding: 12px 14px; display: grid; gap: 2px; }
        .stat .label { font-size: .75rem; text-transform: uppercase; letter-spacing: .05em; color: var(--muted); }
        .stat .value { font-size: 1.35rem; font-weight: 650; font-variant-numeric: tabular-nums; }
        .stat .small { margin: 0; }
        .scroll { overflow-x: auto; background: var(--paper); border: 1px solid var(--rule); border-radius: 8px; }
        table { border-collapse: collapse; width: 100%; font-size: .88rem; }
        th, td { text-align: left; padding: 7px 10px; border-bottom: 1px solid var(--rule); vertical-align: top; }
        th { font-size: .74rem; text-transform: uppercase; letter-spacing: .05em; color: var(--muted); font-weight: 600; background: var(--paper); }
        td.n, th.n { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
        tbody tr:last-child td { border-bottom: 0; }
        .ids { display: block; max-width: 40ch; white-space: normal; word-break: break-word; margin-top: 4px; }
        summary { cursor: pointer; color: var(--accent); }
        summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
        .pill { display: inline-block; padding: 1px 8px; border-radius: 999px; font-size: .78rem; background: var(--rule); color: var(--ink); white-space: nowrap; }
        .pill.yes { background: var(--accent-soft); color: var(--accent); font-weight: 600; }
        .pill.warn { background: var(--warn-soft); color: var(--warn); font-weight: 600; }
        .notes { margin: 12px 0 0; padding-left: 1.2em; color: var(--muted); font-size: .88rem; }
        .map { margin: 0; }
        .mapbox { background: var(--paper); border: 1px solid var(--rule); border-radius: 8px; overflow: hidden; }
        .mapbox svg { display: block; width: 100%; height: auto; max-height: 88vh; }
        .legend { display: flex; flex-wrap: wrap; gap: 6px 16px; font-size: .84rem; margin-top: 10px; }
        .legend span { display: inline-flex; align-items: center; gap: 6px; }
        .key { display: inline-block; width: 14px; height: 10px; border-radius: 2px; vertical-align: middle; margin-right: 6px; }
        .legend .key { margin-right: 0; }
        .k-water { background: var(--water); } .k-rail { background: var(--rail); } .k-major_road { background: var(--major); }
        .k-military { background: var(--military); } .k-cemetery { background: var(--cemetery); } .k-border { background: var(--border); }
        .k-memorial { background: var(--accent); }
        .arena-key { background: none; border: 3px solid var(--ink); height: 8px; }
        .radius-key { background: none; border-top: 2px dashed var(--accent); height: 0; }
        .anchor-key { background: var(--accent); } .reach-key { background: var(--reach); opacity: .5; }
        .trunkmask-key { background: var(--major); height: 4px; } .big-key { background: var(--street-big); height: 3px; }
        .cityline-key { background: none; border-top: 2px solid var(--ink); height: 0; } .stateline-key { background: none; border-top: 2px dashed var(--border); height: 0; }
        .q0 { background: var(--q0); } .q1 { background: var(--q1); } .q2 { background: var(--q2); } .q3 { background: var(--q3); }
        .q4 { background: var(--q4); } .q5 { background: var(--q5); } .q6 { background: var(--q6); } .q7 { background: var(--q7); }
        svg path { fill: none; stroke: none; vector-effect: non-scaling-stroke; }
        svg .reach path { fill: var(--reach); fill-opacity: .32; }
        svg .mask { fill-rule: evenodd; fill-opacity: .72; }
        svg .mask.k-water { fill: var(--water); } svg .mask.k-rail { fill: var(--rail); } svg .mask.k-major_road { fill: var(--major); }
        svg .mask.k-military { fill: var(--military); } svg .mask.k-cemetery { fill: var(--cemetery); } svg .mask.k-border { fill: var(--border); }
        svg .mask.k-memorial { fill: var(--accent); }
        svg .bld { fill: var(--bld); fill-rule: evenodd; }
        svg .st { stroke: var(--street); stroke-width: .6px; stroke-linecap: round; stroke-linejoin: round; }
        svg .st.foot { stroke-width: .35px; stroke-dasharray: 2 2; }
        svg .st.mid { stroke: var(--street-big); stroke-width: 1.1px; }
        svg .st.big { stroke: var(--street-big); stroke-width: 1.8px; }
        svg .st.trunk-mask { stroke: var(--major); stroke-width: 2.2px; }
        svg .quarter { fill-rule: evenodd; fill-opacity: .22; stroke: var(--ink); stroke-width: 1px; stroke-opacity: .6; }
        svg .quarter.q0 { fill: var(--q0); } svg .quarter.q1 { fill: var(--q1); } svg .quarter.q2 { fill: var(--q2); } svg .quarter.q3 { fill: var(--q3); }
        svg .quarter.q4 { fill: var(--q4); } svg .quarter.q5 { fill: var(--q5); } svg .quarter.q6 { fill: var(--q6); } svg .quarter.q7 { fill: var(--q7); }
        svg .arena { fill: none; stroke: var(--ink); stroke-width: 3px; stroke-linejoin: round; }
        svg .radius { stroke: var(--accent); stroke-width: 1.6px; stroke-dasharray: 6 5; }
        svg .anchor { fill: var(--accent); }
        svg .district { stroke: var(--ink); stroke-width: 1.2px; stroke-dasharray: 5 4; stroke-opacity: .7; }
        svg .cityline { stroke: var(--ink); stroke-width: 2px; }
        svg .stateline { stroke: var(--border); stroke-width: 2.2px; stroke-dasharray: 8 4 2 4; }
        svg text { fill: var(--ink); paint-order: stroke; stroke: var(--paper); stroke-width: 4px; stroke-linejoin: round; font-family: var(--font); font-weight: 650; }
        svg .qlabel { font-size: 95px; }
        svg .dlabel { font-size: 520px; fill: var(--muted); stroke-width: 14px; }
        footer { border-top: 1px solid var(--rule); padding-top: 16px; font-size: .85rem; color: var(--muted); display: grid; gap: 6px; }
        footer p { margin: 0; max-width: 90ch; }
        @media (max-width: 520px) { body { font-size: 14px; } .asks { padding: 14px; } }
        </style>
        """;
}
