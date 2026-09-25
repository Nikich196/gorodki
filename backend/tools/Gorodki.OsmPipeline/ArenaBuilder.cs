using Gorodki.Domain.Geo;
using NetTopologySuite.Geometries;

namespace Gorodki.OsmPipeline;

/// <summary>
/// Арена и кварталы по «рецепту» (вопрос 3: граница — по улицам, ~1,5 км — ориентир; кварталы — по крупным улицам).
/// </summary>
/// <remarks>
/// Правило разреза:
/// <list type="number">
/// <item>улицы разреза (по имени или id) и граница города в круге «радиус + 1,5 км» вокруг зданий-якорей узлуются, Polygonize
/// собирает грани — куски города между улицами;</item>
/// <item>грань входит в Арену, если не меньше <see cref="ArenaRecipe.MinShareInside"/> её площади лежит в пределах
/// <see cref="ArenaRecipe.RadiusMeters"/> от якорей;</item>
/// <item>квартал — грань с меткой квартала; грань без метки (полоса между проезжими частями, кольцо развязки) присоединяется
/// к соседнему кварталу с самой длинной общей границей.</item>
/// </list>
/// Геометрия — производная OSM и в git не лежит; в репозитории только рецепт.
/// </remarks>
public static class ArenaBuilder
{
    public sealed record Quarter(string Name, Geometry Shape);

    public sealed record Result(Geometry Arena, IReadOnlyList<Quarter> Quarters, bool Proposal, IReadOnlyList<string> Notes);

    /// <summary>Кварталов по плану — 6–8 (§3.6).</summary>
    public const int MinQuarters = 6;

    public const int MaxQuarters = 8;

    public static Result Build(ArenaRecipe recipe, IReadOnlyList<OsmFeature> features, Geometry city)
    {
        var anchorRefs = new HashSet<string>(recipe.Anchors, StringComparer.Ordinal);
        var anchors = features
            .Where(f => anchorRefs.Contains(f.Ref) && (f.IsArea || f.Geometry is Point))
            .GroupBy(f => f.Ref)
            .Select(g => g.OrderByDescending(f => f.IsArea).First().Geometry)
            .ToList();
        var missing = anchorRefs.Except(features.Where(f => f.IsArea || f.Geometry is Point).Select(f => f.Ref)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Здания Арены не найдены в выгрузке: {string.Join(", ", missing)}.");
        }

        if (anchors.Count == 0)
        {
            throw new InvalidOperationException("В рецепте Арены нет зданий-якорей.");
        }

        var anchorShape = GeoOps.UnionAll(anchors.Select(a => a is Point ? GeoOps.Buffer(a, 1) : a));
        var zone = GeoOps.Buffer(anchorShape, recipe.RadiusMeters);
        var window = GeoOps.Buffer(anchorShape, recipe.RadiusMeters + 1_500);

        var names = new HashSet<string>(recipe.CutStreets, StringComparer.Ordinal);
        var ids = new HashSet<long>(recipe.CutWayIds);
        var cut = features
            .Where(f => f.IsLine && f.Type == "way" && (ids.Contains(f.Id) || f.Tag("name") is { } name && names.Contains(name)))
            .Where(f => f.Geometry.EnvelopeInternal.Intersects(window.EnvelopeInternal))
            .Select(f => GeoOps.LineInside(f.Geometry, window))
            .Where(line => !line.IsEmpty)
            .ToList();
        var unknownStreets = names.Except(features.Where(f => f.IsLine).Select(f => f.Tag("name")).OfType<string>()).Order(StringComparer.Ordinal).ToList();
        if (unknownStreets.Count > 0)
        {
            throw new InvalidOperationException($"Улицы разреза не найдены в выгрузке: {string.Join(", ", unknownStreets)}.");
        }

        cut.Add(window.Boundary);
        cut.Add(GeoOps.LineInside(city.Boundary, window));
        var faces = GeoOps.Polygonize(GeoOps.Node(cut))
            .Where(face => city.Contains(face.Factory.CreatePoint(GeoOps.InteriorPoint(face))))
            .Where(face => GeoOps.Intersection(face, zone).Area >= recipe.MinShareInside * face.Area)
            .OrderBy(face => face.EnvelopeInternal.MinX).ThenBy(face => face.EnvelopeInternal.MinY).ThenBy(face => face.Area)
            .ToList();

        // Метки кварталов → грани.
        var owner = new int?[faces.Count];
        for (var q = 0; q < recipe.Quarters.Count; q++)
        {
            var label = recipe.Quarters[q];
            var (easting, northing) = Utm34.Forward(label.Lat, label.Lon);
            var point = GeoOps.Factory.CreatePoint(new Coordinate(easting, northing));
            var index = faces.FindIndex(face => face.Covers(point));
            if (index < 0)
            {
                throw new InvalidOperationException($"Метка квартала «{label.Name}» не попала ни в одну грань Арены.");
            }

            if (owner[index] is { } other)
            {
                throw new InvalidOperationException($"Метки «{recipe.Quarters[other].Name}» и «{label.Name}» — в одной грани.");
            }

            owner[index] = q;
        }

        // Грани без меток — к соседу с самой длинной общей границей, пока есть что присоединять.
        bool changed;
        do
        {
            changed = false;
            for (var i = 0; i < faces.Count; i++)
            {
                if (owner[i] is not null)
                {
                    continue;
                }

                var best = Enumerable.Range(0, faces.Count)
                    .Where(j => owner[j] is not null && faces[j].EnvelopeInternal.Intersects(faces[i].EnvelopeInternal))
                    .Select(j => (Quarter: owner[j]!.Value, Length: GeoOps.SharedBoundaryLength(faces[i], faces[j])))
                    .Where(x => x.Length > 0)
                    .GroupBy(x => x.Quarter)
                    .Select(g => (Quarter: g.Key, Length: g.Sum(x => x.Length)))
                    .OrderByDescending(x => x.Length).ThenBy(x => x.Quarter)
                    .FirstOrDefault();
                if (best.Length > 0)
                {
                    owner[i] = best.Quarter;
                    changed = true;
                }
            }
        }
        while (changed);

        var orphans = Enumerable.Range(0, faces.Count).Where(i => owner[i] is null).ToList();
        var notes = new List<string>();
        if (orphans.Count > 0)
        {
            notes.Add($"Арена: {orphans.Count} грань(ей) без соседнего квартала не вошли (площадь {orphans.Sum(i => faces[i].Area):F0} м²).");
        }

        var quarters = recipe.Quarters
            .Select((label, q) => new Quarter(label.Name, GeoOps.UnionAll(Enumerable.Range(0, faces.Count).Where(i => owner[i] == q).Select(i => (Geometry)faces[i]))))
            .ToList();
        var arena = GeoOps.UnionAll(quarters.Select(q => q.Shape));
        if (recipe.Proposal)
        {
            notes.Add("Арена и кварталы — предложение Claude, Никита ещё не утвердил (вопрос 3).");
        }

        return new Result(arena, quarters, recipe.Proposal, notes);
    }
}
