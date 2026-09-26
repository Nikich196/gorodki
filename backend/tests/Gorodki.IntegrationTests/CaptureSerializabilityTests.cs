using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Territory;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Union;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// «Одновременные захваты = последовательный повтор» (PLAN.md, §13; §7.3). Несколько игроков замыкают пересекающиеся
/// петли в углу четырёх тайлов, и заявки обрабатываются параллельно. Карта после этого должна совпасть с картой, которую
/// даёт обработка тех же петель по одной в порядке применения (`applied_seq`): блокировки тайлов делают захваты
/// сериализуемыми, а номер применения берётся под ними.
/// </summary>
/// <remarks>
/// Повтор — те же петли в своём месте, сдвинутом ровно на целые километры (та же раскладка по тайлам), с новыми игроками.
/// Карты сравниваются по слоям «владелец, уровень, щит, осада» с точностью до сетки 0,1 м; итоги заявок — по порядку.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class CaptureSerializabilityTests(DatabaseFixture database)
{
    /// <summary>Угол четырёх тайлов: 705 000 E, 5 796 000 N — в метрах от начала прогулок, далеко от мест других тестов.</summary>
    private static readonly (double X, double Y) Corner = (705_000 - RunRequests.WalkOrigin.X, 5_796_000 - RunRequests.WalkOrigin.Y);

    private const int Loops = 4;

    /// <summary>Сдвиг места повтора — целые километры на север.</summary>
    private const double Shift = 3_000;

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Parallel_captures_equal_sequential_replay_in_applied_order(int seed)
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var random = new Random(seed);
        var loops = Enumerable.Range(0, Loops).Select(_ => RandomRectangle(random)).ToArray();
        var origin = (Corner.X + (seed * 10 * Shift), Corner.Y);

        // Параллельно: все заявки поданы, затем обработка всех сразу.
        var parallel = await ClaimAllAsync(api, loops, origin);
        await Task.WhenAll(parallel.Select(c => ProcessAsync(api, c.RunId)));
        var parallelMap = await MapAsync(origin, parallel);
        Assert.Empty(TerritoryInvariants.Check(parallelMap.Territory));
        Assert.DoesNotContain(CaptureStatus.Pending, parallelMap.Statuses);
        Assert.True(
            parallelMap.Layers.Keys.Select(k => k.Owner).Distinct().Count() > 1,
            "Петли должны делить землю между игроками — поменяйте раскладку.");

        // По одной — в порядке применения; неприменённые (отказ) — в конце, их порядок ни на что не влияет.
        var order = Enumerable.Range(0, Loops).OrderBy(i => parallelMap.AppliedSeq[i] ?? long.MaxValue).ToArray();
        var place = (origin.Item1, origin.Item2 + Shift);
        var sequential = await ClaimAllAsync(api, loops, place);
        foreach (var index in order)
        {
            await ProcessAsync(api, sequential[index].RunId);
        }

        var sequentialMap = await MapAsync(place, sequential);
        Assert.Equal(parallelMap.Statuses, sequentialMap.Statuses);
        var difference = Difference(parallelMap, sequentialMap, dy: Shift);
        Assert.True(
            difference < 1.0,
            $"Параллельная карта отличается от повтора в порядке {string.Join("→", order)} на {difference:F1} м²");
    }

    /// <summary>Прямоугольник 80–220 м у угла четырёх тайлов: петли пересекаются между собой и с границами тайлов.</summary>
    private static (double X, double Y)[] RandomRectangle(Random random)
    {
        var width = 80 + random.Next(141);
        var height = 80 + random.Next(141);
        var x = -180 + random.Next(170);
        var y = -180 + random.Next(170);
        return
        [
            (x, y),
            (x + width, y),
            (x + width, y + height),
            (x, y + height),
            (x, y + 1),
        ];
    }

    /// <summary>Новые игроки гуляют петли в месте <paramref name="place"/> и подают заявки (без обработки).</summary>
    private async Task<(Guid RunId, Guid UserId)[]> ClaimAllAsync(
        ApiFactory api, (double X, double Y)[][] loops, (double X, double Y) place)
    {
        var claims = new (Guid RunId, Guid UserId)[loops.Length];
        for (var i = 0; i < loops.Length; i++)
        {
            var (client, userId) = await api.CreatePlayerClientAsync();
            var vertices = loops[i].Select(v => (place.X + v.X, place.Y + v.Y)).ToArray();
            var claim = await WalkAndClaimAsync(Cancel, api, client, vertices);
            claims[i] = (claim.RunId, userId);
        }

        return claims;
    }

    /// <summary>Карта «Бега» в месте <paramref name="place"/> (±1 км) и номера петель владельцев.</summary>
    private async Task<PlaceMap> MapAsync((double X, double Y) place, (Guid RunId, Guid UserId)[] claims)
    {
        var centerX = (int)Math.Floor((RunRequests.WalkOrigin.X + place.X) / 1_000);
        var centerY = (int)Math.Floor((RunRequests.WalkOrigin.Y + place.Y) / 1_000);
        await using var db = database.CreateContext();
        var parcels = await db.Parcels
            .Where(p => p.League == League.Run
                && p.TileX >= centerX - 1 && p.TileX <= centerX + 1
                && p.TileY >= centerY - 1 && p.TileY <= centerY + 1)
            .ToListAsync(Cancel);
        var owners = claims.Select((c, i) => (c.UserId, i)).ToDictionary(x => x.UserId, x => x.i);
        var runIds = claims.Select(c => c.RunId).ToList();
        var captures = await db.Captures
            .Where(c => runIds.Contains(c.RunId))
            .Select(c => new { c.RunId, c.Status, c.AppliedSeq })
            .ToListAsync(Cancel);

        var territory = new TerritoryMap();
        territory.Load(parcels.Select(p => new Parcel(
            new TileKey(p.TileX, p.TileY),
            p.Geometry,
            new ParcelState
            {
                OwnerId = p.OwnerId,
                Level = p.Level,
                LastVisitAt = p.LastVisitAt,
                LastLevelUpAt = p.LastLevelUpAt,
                ShieldUntil = p.ShieldUntil,
                SiegeUntil = p.SiegeUntil,
                LossWindowSince = p.LossWindowSince,
                LossAttackers = AttackerSet.Of(p.LossAttackers),
                TouchedAt = p.TouchedAt,
            })));
        var layers = parcels
            .GroupBy(p => (Owner: owners[p.OwnerId], p.Level, Shield: p.ShieldUntil is not null, Siege: p.SiegeUntil is not null))
            .ToDictionary(g => g.Key, g => UnaryUnionOp.Union(g.Select(p => (Geometry)p.Geometry).ToList()));
        var byLoop = claims.Select(c => captures.Single(s => s.RunId == c.RunId)).ToArray();
        return new PlaceMap(territory, layers, [.. byLoop.Select(c => c.Status)], [.. byLoop.Select(c => c.AppliedSeq)]);
    }

    /// <summary>Сколько квадратных метров земли различается: по каждому слою «владелец, уровень, щит, осада».</summary>
    private static double Difference(PlaceMap parallel, PlaceMap sequential, double dy)
    {
        var back = AffineTransformation.TranslationInstance(0, -dy);
        var total = 0.0;
        foreach (var key in parallel.Layers.Keys.Union(sequential.Layers.Keys))
        {
            var a = parallel.Layers.GetValueOrDefault(key);
            var b = sequential.Layers.TryGetValue(key, out var shifted) ? back.Transform(shifted) : null;
            total += (a, b) switch
            {
                (null, null) => 0,
                (null, _) => b!.Area,
                (_, null) => a.Area,
                _ => a.SymmetricDifference(b).Area,
            };
        }

        return total;
    }

    private sealed record PlaceMap(
        TerritoryMap Territory,
        Dictionary<(int Owner, short Level, bool Shield, bool Siege), Geometry> Layers,
        CaptureStatus[] Statuses,
        long?[] AppliedSeq);
}
