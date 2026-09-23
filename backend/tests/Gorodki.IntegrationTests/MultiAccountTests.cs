using System.Text.Json;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Защита от мультиаккаунтов на настоящей базе (PLAN.md, §3.3): новый аккаунт чужие уровни не снимает, а с одного
/// телефона захваты засчитываются одному аккаунту в сутки.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MultiAccountTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_account_takes_neutral_land_but_leaves_others_land_alone()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync(newcomer: true);
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));

        var claim = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await Walks.ProcessAsync(api, claim.RunId);

        Assert.InRange(await LandAreaAsync(annaId), 9_700, 10_300); // квадрат Анны целый
        Assert.InRange(await LandAreaAsync(borisId), 4_700, 5_300); // Борису — только ничья половина
        var areas = await AreasAsync(claim.CaptureId);
        Assert.InRange(areas["newAccountLimited"], 4_700, 5_300);
        Assert.False(areas.ContainsKey("transferred"));
    }

    [Fact]
    public async Task Account_removes_levels_only_after_48_hours_and_3_km()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync(newcomer: true); // до сдвига часов: токен «из будущего» не прошёл бы проверку
        var area = NewArea();
        api.Time.Advance(TimeSpan.FromHours(49));
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));

        // Аккаунту уже двое суток, но пробег — только эта петля (400 м): уровни ещё не снимает.
        var first = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await Walks.ProcessAsync(api, first.RunId);
        Assert.InRange(await LandAreaAsync(annaId), 9_700, 10_300);

        // Пробег набран (визиты посчитали 3 км прежних забегов) — следующая петля по тому же квадрату забирает L1 Анны.
        await using (var db = database.CreateContext())
        {
            await db.Runs.Where(r => r.Id == first.RunId).ExecuteUpdateAsync(set => set.SetProperty(r => r.AcceptedMeters, 3_000), Cancel);
        }

        api.Time.Advance(TimeSpan.FromMinutes(30));
        var second = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await Walks.ProcessAsync(api, second.RunId);

        Assert.InRange(await LandAreaAsync(annaId), 4_700, 5_300);
        Assert.InRange(await LandAreaAsync(borisId), 9_700, 10_300);
        Assert.InRange((await AreasAsync(second.CaptureId))["transferred"], 4_700, 5_300);
    }

    [Fact]
    public async Task Second_account_on_the_same_phone_is_refused_until_the_next_game_day()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        // Полдень по Минску: все захваты «того же дня» — в одних игровых сутках, где бы ни шёл CI.
        api.Time.SetUtcNow(SeasonCalendar.MinskMidnight(GameClock.GameDayOf(api.Time.GetUtcNow()).AddDays(2)).AddHours(12));
        var phone = Guid.NewGuid();
        var area = NewArea();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100), deviceId: phone)).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));

        var sameDay = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 300, 0, 100), deviceId: phone);
        await Walks.ProcessAsync(api, sameDay.RunId);
        var own = await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 300, 100), deviceId: phone); // своему — можно
        await Walks.ProcessAsync(api, own.RunId);
        api.Time.Advance(TimeSpan.FromDays(1));
        var nextDay = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 300, 0, 100), deviceId: phone);
        await Walks.ProcessAsync(api, nextDay.RunId);

        Assert.Equal((CaptureStatus.Rejected, "device_shared"), await StatusAsync(sameDay.CaptureId));
        Assert.Equal((CaptureStatus.Applied, (string?)null), await StatusAsync(own.CaptureId));
        Assert.Equal((CaptureStatus.Applied, (string?)null), await StatusAsync(nextDay.CaptureId));
        Assert.InRange(await LandAreaAsync(borisId), 9_700, 10_300);
    }

    private async Task<double> LandAreaAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        var parcels = await db.Parcels.Where(p => p.OwnerId == userId).Select(p => p.Geometry).ToListAsync(Cancel);
        return parcels.Sum(g => g.Area);
    }

    private async Task<Dictionary<string, double>> AreasAsync(Guid captureId)
    {
        await using var db = database.CreateContext();
        var json = await db.Captures.Where(c => c.Id == captureId).Select(c => c.AreaByOutcome).SingleAsync(Cancel);
        return JsonSerializer.Deserialize<Dictionary<string, double>>(json!)!;
    }

    private async Task<(CaptureStatus Status, string? Code)> StatusAsync(Guid captureId)
    {
        await using var db = database.CreateContext();
        var capture = await db.Captures.AsNoTracking().SingleAsync(c => c.Id == captureId, Cancel);
        return (capture.Status, capture.RejectCode);
    }
}
