using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gorodki.Api.Features.Clans;
using Gorodki.Api.Features.Territory;
using Gorodki.Domain.Geo;
using Microsoft.EntityFrameworkCore;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Кланы в движке земли (C8, PLAN.md §3.3): земля соклановцев не отбирается, обводя её, игрок освежает им угасание;
/// принадлежность — по текущему составу клана. Освежение — изменение земли, как любое: другие видят его с границы
/// публичности (§3.16), а не сразу.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ClanLandTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_clan_mates_loop_refreshes_land_instead_of_taking_it_until_he_leaves()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (carl, _) = await api.CreatePlayerClientAsync();
        var clan = await (await anna.PostAsJsonAsync("/clans", new CreateClanRequest { Name = $"Земля {Guid.NewGuid().ToString("N")[..8]}" }, Json, Cancel))
            .Content.ReadFromJsonAsync<ClanResponse>(Json, Cancel);
        Assert.Equal(HttpStatusCode.OK, (await boris.PostAsJsonAsync("/clans/join", new JoinClanRequest { Code = clan!.InviteCode! }, Json, Cancel)).StatusCode);

        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        var annaBefore = await LandAsync(annaId);
        api.Time.Advance(TimeSpan.FromHours(1));
        var seenByCarl = await VersionSeenByAsync(carl, tile, known: null);

        // Борис обводит половину земли Анны и столько же ничьей: ничья — его, Аннина остаётся у неё и освежена.
        var mates = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await ProcessAsync(api, mates.RunId);

        Assert.InRange(annaBefore.Area, 9_700, 10_300);
        var annaAfter = await LandAsync(annaId);
        Assert.Equal(annaBefore.Area, annaAfter.Area, 1.0);
        Assert.Equal(annaBefore.TouchedAt, annaAfter.TouchedAt); // освежение соклановцем — не «касание» владельца (§3.4)
        Assert.True(annaAfter.LastVisitAt > annaBefore.LastVisitAt);
        Assert.InRange((await LandAsync(borisId)).Area, 4_700, 5_300);
        var outcome = await OutcomeAsync(mates.CaptureId);
        Assert.InRange(outcome.GetValueOrDefault("refreshedForClanMate"), 4_700, 5_300);
        Assert.False(outcome.ContainsKey("transferred") || outcome.ContainsKey("cracked"));

        // Граница публичности: постороннему тайл до неё «не изменился», после — изменился.
        Assert.Equal(seenByCarl, await VersionSeenByAsync(carl, tile, seenByCarl));
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        Assert.NotEqual(seenByCarl, await VersionSeenByAsync(carl, tile, seenByCarl));

        // Состав — текущий: Борис вышел из клана, и та же петля уже берёт землю Анны (L1 переходит).
        Assert.Equal(HttpStatusCode.NoContent, (await boris.PostAsync("/clans/mine/leave", null, Cancel)).StatusCode);
        api.Time.Advance(TimeSpan.FromHours(1));
        var after = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await ProcessAsync(api, after.RunId);

        Assert.InRange((await LandAsync(annaId)).Area, 4_700, 5_300);
        Assert.InRange((await OutcomeAsync(after.CaptureId)).GetValueOrDefault("transferred"), 4_700, 5_300);
    }

    private async Task<(double Area, DateTimeOffset LastVisitAt, DateTimeOffset TouchedAt)> LandAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        var parcels = await db.Parcels.AsNoTracking().Where(p => p.OwnerId == userId).ToListAsync(Cancel);
        return parcels.Count == 0
            ? (0, default, default)
            : (parcels.Sum(p => p.Geometry.Area), parcels.Max(p => p.LastVisitAt), parcels.Max(p => p.TouchedAt));
    }

    private async Task<Dictionary<string, double>> OutcomeAsync(Guid captureId)
    {
        await using var db = database.CreateContext();
        var json = await db.Captures.Where(c => c.Id == captureId).Select(c => c.AreaByOutcome).SingleAsync(Cancel);
        return JsonSerializer.Deserialize<Dictionary<string, double>>(json!)!;
    }

    /// <summary>Видимая зрителю версия тайла: «не изменился» — та же, что он знает.</summary>
    private async Task<long> VersionSeenByAsync(HttpClient viewer, TileKey tile, long? known)
    {
        var query = known is { } version ? $"{tile.X}:{tile.Y}@{version}" : $"{tile.X}:{tile.Y}";
        var seen = (await viewer.GetFromJsonAsync<TerritoryResponse>($"/territory?league=run&tiles={query}", Json, Cancel))!;
        return seen.Tiles.SingleOrDefault()?.Version ?? known!.Value;
    }
}
