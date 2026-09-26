using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorodki.Domain.Fog;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Osm;
using NetTopologySuite.Geometries;

namespace Gorodki.OsmPipeline;

/// <summary>Вход и инструменты, из которых собран набор: «метод» для ODbL 4.6 и условие повторяемости сборки.</summary>
public sealed record SetSource
{
    /// <summary>Файл выгрузки Geofabrik, например <c>belarus-latest.osm.pbf</c>.</summary>
    public string File { get; init; } = "";

    /// <summary>SHA-256 файла выгрузки.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>Дата репликации из заголовка файла (<c>osmosis_replication_timestamp</c>).</summary>
    public DateTimeOffset? ReplicationTimestamp { get; init; }

    /// <summary>Версии: osmium, libosmium, NetTopologySuite, .NET.</summary>
    public SortedDictionary<string, string> Tools { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>Набор, прочитанный из файла: содержимое, метаданные и отпечаток.</summary>
public sealed record OsmSetFileContent(int Version, OsmSetData Data, SetSource Source, JsonObject Metadata, string Fingerprint, DateTimeOffset BuiltAt);

/// <summary>
/// Файл набора <c>osm-set-N.zip</c> (osm-pipeline.md, «Поток данных», шаг 6): <c>metadata.json</c>, куски масок в TWKB по
/// тайлам, биты клеток (Deflate, как <c>fog_tiles</c>), районы и файл лицензии. В git не коммитится — это производная
/// база OSM под ODbL, публикуется отдельным релизом (вопрос 5, А).
/// </summary>
/// <remarks>
/// Отпечаток (SHA-256) считается по содержимому — распакованные биты клеток, TWKB, параметры, — а не по сжатым байтам и
/// без времени сборки: Deflate зависит от версии zlib в .NET, и другая версия может сжать те же биты иначе.
/// </remarks>
public static class OsmSetFile
{
    public const string Format = "gorodki-osm-set/1";

    public const string Attribution = "© участники OpenStreetMap (https://www.openstreetmap.org/copyright). Данные — под лицензией Open Database License (ODbL) 1.0.";

    private const string License = """
        Набор osm-set — производная база данных OpenStreetMap.

        © участники OpenStreetMap — https://www.openstreetmap.org/copyright
        Лицензия: Open Data Commons Open Database License (ODbL) 1.0 — https://opendatacommons.org/licenses/odbl/1-0/

        Исходные данные: выгрузка Geofabrik (https://download.geofabrik.de/europe/belarus.html), дата и SHA-256 — в metadata.json.
        Метод получения (ODbL 4.6): параметры и версии инструментов — в metadata.json; описание конвейера —
        docs/architecture/osm-pipeline.md проекта «Городки».
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // ── Отпечаток ───────────────────────────────────────────────────────────

    public static string Fingerprint(OsmSetData data, string canonicalParams)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Text(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        void Bytes(byte[] value)
        {
            hash.AppendData(BitConverter.GetBytes(value.Length));
            hash.AppendData(value);
        }

        Text(Format);
        Text(canonicalParams);
        Text($"frame {data.Frame.MinX} {data.Frame.MinY} {data.Frame.MaxX} {data.Frame.MaxY} {OsmCodes.Of(data.PlayZone)}");
        foreach (var piece in data.Masks)
        {
            Text($"mask {OsmCodes.Of(piece.Kind)} {piece.Tile}");
            Bytes(Twkb.Write(piece.Geometry));
        }

        foreach (var piece in data.Land)
        {
            Text($"land {(short)piece.Kind} {piece.Tile}");
            Bytes(Twkb.Write(piece.Geometry));
        }

        foreach (var (key, bits) in data.Reachable)
        {
            Text($"reachable {key.X} {key.Y}");
            Bytes(bits.ToBytes());
        }

        foreach (var district in data.Districts)
        {
            Text(string.Create(
                CultureInfo.InvariantCulture,
                $"district {district.Key} {OsmCodes.Of(district.Kind)} {district.Name} {district.OsmId} {district.Proposal} {district.AreaWithoutMasks:R}"));
            Bytes(Twkb.Write(district.Geometry));
            foreach (var (key, bits) in district.Tiles)
            {
                Text($"tile {key.X} {key.Y}");
                Bytes(bits.ToBytes());
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // ── Запись ──────────────────────────────────────────────────────────────

    public static JsonObject Metadata(int version, OsmSetData data, PipelineParams parameters, SetSource source, string fingerprint, DateTimeOffset builtAt)
    {
        var masks = new JsonObject();
        foreach (var group in data.Masks.GroupBy(m => m.Kind).OrderBy(g => g.Key))
        {
            masks[OsmCodes.Of(group.Key)] = new JsonObject
            {
                ["pieces"] = group.Count(),
                ["vertices"] = group.Sum(m => m.Geometry.NumPoints),
                ["areaSquareMeters"] = Math.Round(group.Sum(m => m.Geometry.Area), 1),
            };
        }

        var districts = new JsonArray();
        foreach (var district in data.Districts)
        {
            districts.Add(new JsonObject
            {
                ["key"] = district.Key,
                ["kind"] = OsmCodes.Of(district.Kind),
                ["name"] = district.Name,
                ["osmId"] = district.OsmId,
                ["proposal"] = district.Proposal,
                ["areaSquareMeters"] = Math.Round(district.Geometry.Area, 1),
                ["areaWithoutMasksSquareMeters"] = district.AreaWithoutMasks,
                ["reachableCells"] = district.CellCount,
            });
        }

        return new JsonObject
        {
            ["format"] = Format,
            ["version"] = version,
            ["fingerprint"] = fingerprint,
            ["builtAt"] = builtAt.ToString("O", CultureInfo.InvariantCulture),
            ["attribution"] = Attribution,
            ["source"] = JsonSerializer.SerializeToNode(source, Json),
            ["params"] = JsonNode.Parse(parameters.ToCanonicalJson()),
            ["frame"] = new JsonObject
            {
                ["minTileX"] = data.Frame.MinX,
                ["minTileY"] = data.Frame.MinY,
                ["maxTileX"] = data.Frame.MaxX,
                ["maxTileY"] = data.Frame.MaxY,
            },
            ["playZone"] = OsmCodes.Of(data.PlayZone),
            ["counts"] = new JsonObject
            {
                ["masks"] = masks,
                ["landPieces"] = data.Land.Count,
                ["reachableTiles"] = data.Reachable.Count,
                ["reachableCells"] = data.ReachableCells,
                ["districts"] = districts,
            },
            ["notes"] = new JsonArray([.. data.Notes.Select(n => (JsonNode)n)]),
        };
    }

    public static void Write(string path, int version, OsmSetData data, PipelineParams parameters, SetSource source, DateTimeOffset builtAt)
    {
        var fingerprint = Fingerprint(data, parameters.ToCanonicalJson());
        var metadata = Metadata(version, data, parameters, source, fingerprint, builtAt);
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Entry(zip, "metadata.json", metadata.ToJsonString(Json));
        Entry(zip, "LICENSE.txt", License);
        Entry(zip, "masks.jsonl", Lines(data.Masks.Select(m => new JsonObject
        {
            ["kind"] = OsmCodes.Of(m.Kind),
            ["x"] = m.Tile.X,
            ["y"] = m.Tile.Y,
            ["twkb"] = Convert.ToBase64String(Twkb.Write(m.Geometry)),
        })));
        Entry(zip, "land.jsonl", Lines(data.Land.Select(m => new JsonObject
        {
            ["kind"] = (short)m.Kind,
            ["x"] = m.Tile.X,
            ["y"] = m.Tile.Y,
            ["twkb"] = Convert.ToBase64String(Twkb.Write(m.Geometry)),
        })));
        Entry(zip, "reachable.jsonl", Lines(data.Reachable.Select(t => Tile(t.Key, t.Value))));
        Entry(zip, "districts.jsonl", Lines(data.Districts.Select(d => new JsonObject
        {
            ["key"] = d.Key,
            ["kind"] = OsmCodes.Of(d.Kind),
            ["name"] = d.Name,
            ["osmId"] = d.OsmId,
            ["proposal"] = d.Proposal,
            ["areaWithoutMasks"] = d.AreaWithoutMasks,
            ["twkb"] = Convert.ToBase64String(Twkb.Write(d.Geometry)),
            ["tiles"] = new JsonArray([.. d.Tiles.Select(t => (JsonNode)Tile(t.Key, t.Value))]),
        })));
    }

    private static JsonObject Tile(FogTileKey key, FogTileBits bits) => new()
    {
        ["x"] = key.X,
        ["y"] = key.Y,
        ["cells"] = bits.Count,
        ["bits"] = Convert.ToBase64String(FogTileCodec.Compress(bits)),
    };

    private static string Lines(IEnumerable<JsonObject> objects) =>
        string.Concat(objects.Select(o => o.ToJsonString() + "\n"));

    private static void Entry(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); // время сборки — в metadata.json, не в zip
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    // ── Чтение ──────────────────────────────────────────────────────────────

    /// <exception cref="FormatException">Не набор, другой формат или отпечаток не сходится с содержимым.</exception>
    public static OsmSetFileContent Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        string Text(string name) =>
            zip.GetEntry(name) is { } entry ? new StreamReader(entry.Open(), Encoding.UTF8).ReadToEnd() : throw new FormatException($"В наборе нет {name}.");
        IEnumerable<JsonObject> JsonLines(string name) =>
            Text(name).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject());

        var metadata = JsonNode.Parse(Text("metadata.json"))!.AsObject();
        if ((string?)metadata["format"] != Format)
        {
            throw new FormatException($"Формат набора не {Format}.");
        }

        var frame = metadata["frame"]!.AsObject();
        var masks = JsonLines("masks.jsonl")
            .Select(o => new MaskPiece(OsmCodes.MaskKindOf((string)o["kind"]!), new TileKey((int)o["x"]!, (int)o["y"]!), (Polygon)Twkb.Read(Convert.FromBase64String((string)o["twkb"]!))))
            .ToList();
        var land = JsonLines("land.jsonl")
            .Select(o => new LandPiece((LandKind)(short)o["kind"]!, new TileKey((int)o["x"]!, (int)o["y"]!), (Polygon)Twkb.Read(Convert.FromBase64String((string)o["twkb"]!))))
            .ToList();
        var reachable = new SortedDictionary<FogTileKey, FogTileBits>(JsonLines("reachable.jsonl").ToDictionary(TileKeyOf, BitsOf));
        var districts = JsonLines("districts.jsonl")
            .Select(o => new DistrictData(
                (string)o["key"]!,
                OsmCodes.DistrictKindOf((string)o["kind"]!),
                (string)o["name"]!,
                (long?)o["osmId"],
                (bool)o["proposal"]!,
                Twkb.Read(Convert.FromBase64String((string)o["twkb"]!)),
                (double)o["areaWithoutMasks"]!,
                new SortedDictionary<FogTileKey, FogTileBits>(o["tiles"]!.AsArray().Select(t => t!.AsObject()).ToDictionary(TileKeyOf, BitsOf))))
            .ToList();
        var data = new OsmSetData
        {
            Frame = new TileRange((int)frame["minTileX"]!, (int)frame["minTileY"]!, (int)frame["maxTileX"]!, (int)frame["maxTileY"]!),
            PlayZone = OsmCodes.PlayZoneOf((string)metadata["playZone"]!),
            Masks = masks,
            Land = land,
            Reachable = reachable,
            Districts = districts,
            Notes = metadata["notes"]!.AsArray().Select(n => (string)n!).ToList(),
        };

        var parameters = PipelineParams.FromJson(metadata["params"]!.ToJsonString());
        var fingerprint = Fingerprint(data, parameters.ToCanonicalJson());
        if (fingerprint != (string?)metadata["fingerprint"])
        {
            throw new FormatException("Отпечаток набора не сходится с содержимым: файл повреждён или изменён.");
        }

        var source = metadata["source"].Deserialize<SetSource>(Json) ?? new SetSource();
        return new OsmSetFileContent((int)metadata["version"]!, data, source, metadata, fingerprint, DateTimeOffset.Parse((string)metadata["builtAt"]!, CultureInfo.InvariantCulture));
    }

    private static FogTileKey TileKeyOf(JsonObject o) => new((int)o["x"]!, (int)o["y"]!);

    private static FogTileBits BitsOf(JsonObject o)
    {
        var bits = FogTileCodec.Decompress(Convert.FromBase64String((string)o["bits"]!));
        return bits.Count == (int)o["cells"]! ? bits : throw new FormatException("Число клеток тайла не сходится с битами.");
    }
}
