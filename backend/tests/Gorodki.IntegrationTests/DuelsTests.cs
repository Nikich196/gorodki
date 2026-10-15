using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Duels;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Social;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Duels;
using Gorodki.Domain.Leagues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Дуэли — <c>/duels</c> (PLAN.md, §3.14; задача #147 — сделано Claude 15.10): вызов и ответ, лимиты, «одно устройство —
/// один аккаунт», живой счёт по границе публичности, судья (задача Hangfire): истёкшие вызовы, смена лидера, итог через 4 ч
/// после конца — один раз.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class DuelsTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Challenge_is_accepted_by_the_friend_and_a_pair_duels_once_a_week()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, boris);

        var challenge = await ChallengeAsync(anna, borisId, days: 3);
        Assert.Equal(HttpStatusCode.Created, challenge.StatusCode);
        var pending = await Problems.BodyAsync<DuelResponse>(challenge, Cancel);
        Assert.Equal((DuelStatus.Pending, (long?)null), (pending.Status, pending.StartsAtMs));
        Assert.False(pending.Incoming); // Анна вызвала — кнопок «Принять» у неё нет
        Assert.True((await boris.GetFromJsonAsync<DuelResponse>($"/duels/{pending.Id}", Json, Cancel))!.Incoming);

        var accepted = await Problems.BodyAsync<DuelResponse>(
            await boris.PostAsJsonAsync($"/duels/{pending.Id}/accept", new AcceptDuelRequest(), Json, Cancel), Cancel);

        var now = api.Time.GetUtcNow();
        Assert.Equal(DuelStatus.Active, accepted.Status);
        Assert.Equal((now.ToUnixTimeMilliseconds(), now.AddDays(3).ToUnixTimeMilliseconds()), (accepted.StartsAtMs, accepted.EndsAtMs));
        Assert.Equal(borisId, accepted.Me.PlayerId); // «я» — всегда спрашивающий
        Assert.True(accepted.Incoming);
        Assert.Equal((0.0, 0.0), (accepted.Me.Score, accepted.Opponent.Score)); // идёт — счёт 0 : 0, а не «нет результата»
        Assert.Contains((await anna.GetFromJsonAsync<DuelsResponse>("/duels", Json, Cancel))!.Duels, d => d.Id == pending.Id);
        Assert.Equal((409, "duel_pair_week"), await Problems.OfAsync(await ChallengeAsync(anna, borisId, days: 1), Cancel));
        Assert.Equal((409, "duel_pair_week"), await Problems.OfAsync(await ChallengeAsync(boris, (await IdOfAsync(anna)), days: 1), Cancel));
    }

    [Fact]
    public async Task Strangers_young_accounts_and_wrong_terms_are_refused()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (_, carlId) = await api.CreatePlayerClientAsync(); // не друг
        var (young, youngId) = await api.CreatePlayerClientAsync(newcomer: true);
        await BefriendAsync(anna, boris);
        await BefriendAsync(young, boris);

        Assert.Equal((404, "duel_opponent_not_found"), await Problems.OfAsync(await ChallengeAsync(anna, carlId, days: 3), Cancel));
        Assert.Equal((404, "duel_opponent_not_found"), await Problems.OfAsync(await ChallengeAsync(anna, annaId, days: 3), Cancel));
        Assert.Equal((400, "duel_invalid"), await Problems.OfAsync(await ChallengeAsync(anna, borisId, days: 2), Cancel));
        Assert.Equal((409, "duel_account_too_new"), await Problems.OfAsync(await ChallengeAsync(young, borisId, days: 1), Cancel));
        Assert.Equal((409, "duel_account_too_new"), await Problems.OfAsync(await ChallengeAsync(boris, youngId, days: 1), Cancel));
        var noSegment = new CreateDuelRequest { OpponentId = borisId, League = League.Run, Metric = DuelMetric.SegmentTime, Days = 1 };
        var segmentless = await anna.PostAsJsonAsync("/duels", noSegment, Json, Cancel);
        Assert.Equal((400, "duel_invalid"), await Problems.OfAsync(segmentless, Cancel)); // без отрезка

        // Отрезков на сервере ещё нет (#146): вызвать на отрезок нельзя; отрезок у другой метрики — тоже ошибка.
        var segment = await anna.PostAsJsonAsync("/duels", noSegment with { SegmentId = Guid.NewGuid() }, Json, Cancel);
        Assert.Equal((400, "duel_invalid"), await Problems.OfAsync(segment, Cancel));
        var stray = new CreateDuelRequest { OpponentId = borisId, League = League.Run, Metric = DuelMetric.Distance, Days = 1, SegmentId = Guid.NewGuid() };
        Assert.Equal((400, "duel_invalid"), await Problems.OfAsync(await anna.PostAsJsonAsync("/duels", stray, Json, Cancel), Cancel));

        // Рюкзака ещё нет (#140): ставка — честный отказ, и ничего не создано; без ставки — можно.
        var staked = new CreateDuelRequest { OpponentId = borisId, League = League.Run, Metric = DuelMetric.Distance, Days = 1, StakeItemId = Guid.NewGuid() };
        Assert.Equal((409, "duel_stake_invalid"), await Problems.OfAsync(await anna.PostAsJsonAsync("/duels", staked, Json, Cancel), Cancel));
        Assert.Empty((await anna.GetFromJsonAsync<DuelsResponse>("/duels", Json, Cancel))!.Duels);
        var duel = await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(anna, borisId, days: 1), Cancel);
        var stakedAccept = await boris.PostAsJsonAsync($"/duels/{duel.Id}/accept", new AcceptDuelRequest { StakeItemId = Guid.NewGuid() }, Json, Cancel);
        Assert.Equal((409, "duel_stake_invalid"), await Problems.OfAsync(stakedAccept, Cancel));
        Assert.Equal(DuelStatus.Pending, (await boris.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!.Status);
    }

    [Fact]
    public async Task At_most_two_duels_go_at_once_and_a_stranger_sees_none_of_them()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (stranger, _) = await api.CreatePlayerClientAsync();
        Guid first = default;
        for (var i = 0; i < DuelEndpoints.MaxActive; i++)
        {
            var (friend, friendId) = await api.CreatePlayerClientAsync();
            await BefriendAsync(anna, friend);
            var duel = await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(anna, friendId, days: 7), Cancel);
            await friend.PostAsJsonAsync($"/duels/{duel.Id}/accept", new AcceptDuelRequest(), Json, Cancel);
            first = first == default ? duel.Id : first;
        }

        var (third, thirdId) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, third);

        Assert.Equal((409, "duel_limit"), await Problems.OfAsync(await ChallengeAsync(anna, thirdId, days: 1), Cancel));
        Assert.Equal((404, "duel_not_found"), await Problems.OfAsync(await stranger.GetAsync($"/duels/{first}", Cancel), Cancel));
        Assert.Equal((404, "duel_not_found"), await Problems.OfAsync(
            await stranger.PostAsJsonAsync($"/duels/{first}/accept", new AcceptDuelRequest(), Json, Cancel), Cancel));
        Assert.Equal((404, "duel_not_found"), await Problems.OfAsync(await stranger.PostAsync($"/duels/{first}/decline", null, Cancel), Cancel));

        // Вызов Анне от третьего друга создаётся (его место свободно), но принять его Анна не может — у неё уже две.
        var incoming = await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(third, await IdOfAsync(anna), days: 1), Cancel);
        Assert.Equal((409, "duel_limit"), await Problems.OfAsync(
            await anna.PostAsJsonAsync($"/duels/{incoming.Id}/accept", new AcceptDuelRequest(), Json, Cancel), Cancel));
    }

    [Fact]
    public async Task Own_unanswered_challenges_take_places_too()
    {
        // Иначе можно разослать десять вызовов и получить десять дуэлей разом.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var friends = new List<(HttpClient Client, Guid Id)>();
        for (var i = 0; i < DuelEndpoints.MaxActive + 1; i++)
        {
            var friend = await api.CreatePlayerClientAsync();
            await BefriendAsync(anna, friend.Client);
            friends.Add(friend);
        }

        var sent = new List<DuelResponse>();
        for (var i = 0; i < DuelEndpoints.MaxActive; i++)
        {
            sent.Add(await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(anna, friends[i].Id, days: 1), Cancel));
        }

        Assert.Equal((409, "duel_limit"), await Problems.OfAsync(await ChallengeAsync(anna, friends[^1].Id, days: 1), Cancel));
        Assert.Equal(HttpStatusCode.NoContent, (await friends[0].Client.PostAsync($"/duels/{sent[0].Id}/decline", null, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ChallengeAsync(anna, friends[^1].Id, days: 1)).StatusCode);
    }

    [Fact]
    public async Task Only_the_challenged_answers_and_an_unanswered_challenge_expires_after_a_day()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, boris);
        var duel = await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(anna, borisId, days: 1), Cancel);

        // Свой вызов не принять и не отклонить — отвечает только вызванный.
        Assert.Equal((404, "duel_not_found"), await Problems.OfAsync(
            await anna.PostAsJsonAsync($"/duels/{duel.Id}/accept", new AcceptDuelRequest(), Json, Cancel), Cancel));
        Assert.Equal((404, "duel_not_found"), await Problems.OfAsync(await anna.PostAsync($"/duels/{duel.Id}/decline", null, Cancel), Cancel));

        Assert.Equal(HttpStatusCode.NoContent, (await boris.PostAsync($"/duels/{duel.Id}/decline", null, Cancel)).StatusCode);
        Assert.Equal(DuelStatus.Declined, (await anna.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!.Status);
        Assert.Equal((404, "duel_not_found"), await Problems.OfAsync(await boris.PostAsync($"/duels/{duel.Id}/decline", null, Cancel), Cancel));
        Assert.Equal((404, "duel_not_found"), await Problems.OfAsync(
            await boris.PostAsJsonAsync($"/duels/{duel.Id}/accept", new AcceptDuelRequest(), Json, Cancel), Cancel));

        // Отказ — не дуэль: «раз в неделю» он не тратит. Новый вызов без ответа через сутки — отклонён.
        var again = await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(anna, borisId, days: 1), Cancel);
        api.Time.Advance(DuelRules.PendingLifetime + TimeSpan.FromMinutes(1));
        Assert.Equal((404, "duel_not_found"), await Problems.OfAsync(
            await boris.PostAsJsonAsync($"/duels/{again.Id}/accept", new AcceptDuelRequest(), Json, Cancel), Cancel));
        Assert.Equal(DuelStatus.Declined, (await anna.GetFromJsonAsync<DuelResponse>($"/duels/{again.Id}", Json, Cancel))!.Status);

        Assert.True(await RefereeAsync(api, r => r.ExpirePendingAsync(CancellationToken.None)) >= 1);
        Assert.Equal(DuelStatus.Declined, (await StoredAsync(again.Id)).Status);
        Assert.Equal(HttpStatusCode.Created, (await ChallengeAsync(anna, borisId, days: 1)).StatusCode);
    }

    [Fact]
    public async Task Live_score_shows_the_opponents_capture_only_after_the_public_boundary()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, boris);
        var duel = await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(anna, borisId, days: 1, DuelMetric.CapturedArea), Cancel);
        await boris.PostAsJsonAsync($"/duels/{duel.Id}/accept", new AcceptDuelRequest(), Json, Cancel);
        api.Time.Advance(TimeSpan.FromMinutes(25)); // прогулка начата 20 минут назад — уже после принятия вызова

        // Борис взял землю — захват ещё скрыт: по счёту дуэли нельзя понять, что он бежит прямо сейчас.
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(NewArea(), 0, 0, 100))).RunId);
        var hidden = (await anna.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!;
        var own = (await boris.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!;
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var shown = (await anna.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!;

        Assert.True(hidden.Opponent.Score is null or 0, $"Счёт соперника до границы: {hidden.Opponent.Score}");
        Assert.InRange(own.Me.Score ?? 0, 9_500, 10_500); // свой — сразу, как «взятое» в итоге заявки
        Assert.InRange(shown.Opponent.Score ?? 0, 9_500, 10_500);
    }

    [Fact]
    public async Task Loop_closed_before_the_duel_started_does_not_count()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, boris);
        var duel = await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(anna, borisId, days: 1, DuelMetric.CapturedArea), Cancel);

        // Прогулка начата 20 минут назад и петля замкнута до принятия; применена (пришла из офлайна) — уже после.
        var claim = await WalkAndClaimAsync(Cancel, api, boris, Square(NewArea(), 0, 0, 100));
        await boris.PostAsJsonAsync($"/duels/{duel.Id}/accept", new AcceptDuelRequest(), Json, Cancel);
        await ProcessAsync(api, claim.RunId);

        Assert.Equal(0, (await boris.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!.Me.Score);
    }

    [Fact]
    public async Task Lead_change_is_announced_from_what_both_can_see_and_only_once()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, boris);
        var duel = await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(anna, borisId, days: 1, DuelMetric.CapturedArea), Cancel);
        await boris.PostAsJsonAsync($"/duels/{duel.Id}/accept", new AcceptDuelRequest(), Json, Cancel);
        api.Time.Advance(TimeSpan.FromMinutes(25));
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(NewArea(), 0, 0, 100))).RunId);
        var notifier = new RecordingNotifier();

        // До границы впереди никто: свежий захват Бориса видит только он сам. База общая — судья проходит и по дуэлям
        // соседних тестов, поэтому смотрим только на свою.
        await RefereeAsync(api, r => r.TrackLeadersAsync(CancellationToken.None), notifier);
        Assert.DoesNotContain(notifier.Leaders, lead => lead.Duel == duel.Id);
        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        await RefereeAsync(api, r => r.TrackLeadersAsync(CancellationToken.None), notifier);
        await RefereeAsync(api, r => r.TrackLeadersAsync(CancellationToken.None), notifier); // повтор — без второго уведомления

        Assert.Equal([(duel.Id, borisId)], notifier.Leaders.Where(lead => lead.Duel == duel.Id));
        Assert.Equal(DuelSideKind.Opponent, (await StoredAsync(duel.Id)).Leader);
    }

    [Fact]
    public async Task Referee_settles_after_the_grace_once_and_the_winner_starts_a_streak()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, boris);
        var duel = await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(anna, borisId, days: 1, DuelMetric.Distance), Cancel);
        var accepted = await Problems.BodyAsync<DuelResponse>(
            await boris.PostAsJsonAsync($"/duels/{duel.Id}/accept", new AcceptDuelRequest(), Json, Cancel), Cancel);
        api.Time.Advance(TimeSpan.FromMinutes(25));

        // Борис прошёл 700 м: дистанция — когда визиты посчитаны, то есть конец забега уже публичен.
        var run = await WalkAndFinishAsync(Cancel, api, boris, [(0, 0), (700, 0)]);
        Assert.Equal(0, (await anna.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!.Opponent.Score);
        api.Time.Advance(TerritoryReader.PublicDelay);
        Assert.NotNull(await VisitAsync(api, run.Id));
        Assert.InRange((await anna.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!.Opponent.Score ?? 0, 690, 710);

        // Срок кончился, но итог ещё не подведён: ждём поздние данные.
        api.Time.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(accepted.EndsAtMs!.Value) + DuelReferee.SettleDelay - TimeSpan.FromMinutes(1));
        await RefereeAsync(api, r => r.RunAsync(CancellationToken.None));
        Assert.Equal(DuelStatus.Active, (await anna.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!.Status);

        api.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await RefereeAsync(api, r => r.SettleDueAsync(CancellationToken.None)) >= 1);
        var finished = (await anna.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!;
        var stored = await StoredAsync(duel.Id);
        await RefereeAsync(api, r => r.SettleDueAsync(CancellationToken.None)); // повтор ничего не меняет

        Assert.Equal((DuelStatus.Finished, borisId), (finished.Status, finished.WinnerId));
        Assert.Equal(0, finished.Me.Score);
        Assert.InRange(finished.Opponent.Score ?? 0, 690, 710);
        Assert.Equal((DuelOutcome.OpponentWon, (short?)1), (stored.Outcome, stored.WinnerStreak));
        var again = await StoredAsync(duel.Id);
        Assert.Equal((stored.FinishedAt, stored.OpponentScore, stored.WinnerStreak), (again.FinishedAt, again.OpponentScore, again.WinnerStreak));
        await using var db = database.CreateContext();
        Assert.Equal((1, DuelBadge.Bronze), await DuelStreaks.CurrentAsync(db, borisId, Cancel));
        Assert.Equal((0, DuelBadge.None), await DuelStreaks.CurrentAsync(db, await IdOfAsync(anna), Cancel));
    }

    [Fact]
    public async Task Nobody_moved_so_the_duel_ends_in_a_draw()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, boris);
        var duel = await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(anna, borisId, days: 1, DuelMetric.ExploredArea), Cancel);
        await boris.PostAsJsonAsync($"/duels/{duel.Id}/accept", new AcceptDuelRequest(), Json, Cancel);

        api.Time.Advance(TimeSpan.FromDays(1) + DuelReferee.SettleDelay);
        await RefereeAsync(api, r => r.SettleDueAsync(CancellationToken.None));

        var finished = (await boris.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!;
        Assert.Equal((DuelStatus.Finished, (Guid?)null, 0.0, 0.0), (finished.Status, finished.WinnerId, finished.Me.Score, finished.Opponent.Score));
        var stored = await StoredAsync(duel.Id);
        Assert.Equal((DuelOutcome.Draw, (short?)null), (stored.Outcome, stored.WinnerStreak));
        // Ничья — тоже дуэль: до конца недели с начала той пара снова не сойдётся.
        Assert.Equal((409, "duel_pair_week"), await Problems.OfAsync(await ChallengeAsync(anna, borisId, days: 1), Cancel));
        api.Time.Advance(DuelRules.PairCooldown);
        Assert.Equal(HttpStatusCode.Created, (await ChallengeAsync(anna, borisId, days: 1)).StatusCode);
    }

    [Fact]
    public async Task Explored_area_counts_own_fog_at_once_and_the_opponents_after_the_run_end_is_public()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, boris);
        var duel = await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(anna, borisId, days: 3, DuelMetric.ExploredArea), Cancel);
        await boris.PostAsJsonAsync($"/duels/{duel.Id}/accept", new AcceptDuelRequest(), Json, Cancel);
        api.Time.Advance(TimeSpan.FromMinutes(25));

        var run = await WalkAndFinishAsync(Cancel, api, boris, [(0, 0), (700, 0)]);
        int cells;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            cells = (await scope.ServiceProvider.GetRequiredService<FogProcessor>().StampRunAsync(run.Id, Cancel))!.Value;
        }

        var expected = Math.Round(cells * DuelScores.FogCellSquareMeters, 1);
        Assert.True(cells > 0);
        Assert.Equal(expected, (await boris.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!.Me.Score);
        Assert.Equal(0, (await anna.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!.Opponent.Score);

        api.Time.Advance(TerritoryReader.PublicDelay);
        Assert.NotNull(await VisitAsync(api, run.Id));
        Assert.Equal(expected, (await anna.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!.Opponent.Score);
    }

    [Fact]
    public async Task Accounts_that_ran_from_one_phone_cannot_duel_and_a_shared_phone_mid_duel_cancels_it()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (vera, veraId) = await api.CreatePlayerClientAsync();
        await BefriendAsync(anna, boris);
        await BefriendAsync(anna, vera);
        var phone = Guid.NewGuid();
        await AddRunAsync(annaId, phone);
        await AddRunAsync(borisId, phone);

        Assert.Equal((404, "duel_opponent_not_found"), await Problems.OfAsync(await ChallengeAsync(anna, borisId, days: 1), Cancel));

        // С Верой телефоны разные — дуэль идёт; по ходу Вера побегала с телефона Анны — итога нет, дуэль снята.
        var duel = await Problems.BodyAsync<DuelResponse>(await ChallengeAsync(anna, veraId, days: 1), Cancel);
        await vera.PostAsJsonAsync($"/duels/{duel.Id}/accept", new AcceptDuelRequest(), Json, Cancel);
        await AddRunAsync(veraId, phone);
        api.Time.Advance(TimeSpan.FromDays(1) + DuelReferee.SettleDelay);
        await RefereeAsync(api, r => r.SettleDueAsync(CancellationToken.None));

        var finished = (await vera.GetFromJsonAsync<DuelResponse>($"/duels/{duel.Id}", Json, Cancel))!;
        Assert.Equal((DuelStatus.Finished, (Guid?)null), (finished.Status, finished.WinnerId));
        Assert.Equal(DuelOutcome.Cancelled, (await StoredAsync(duel.Id)).Outcome);
    }

    [Fact]
    public async Task Segment_time_comes_from_the_segments_hook_own_at_once_and_the_opponents_only_public()
    {
        // Отрезков на сервере ещё нет (#146): счёт segmentTime — через IDuelSegments; без проходов — null.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        await using var scope = api.Services.CreateAsyncScope();
        var segments = new FakeSegments();
        var scores = new DuelScores(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<GameConfigStore>(), segments);
        var (anna, boris) = (Guid.NewGuid(), Guid.NewGuid());
        var start = api.Time.GetUtcNow();
        var duel = new DuelEntity
        {
            Id = Guid.NewGuid(),
            ChallengerId = anna,
            OpponentId = boris,
            League = League.Bike,
            Metric = DuelMetric.SegmentTime,
            SegmentId = Guid.NewGuid(),
            Days = 3,
            Status = DuelStatus.Active,
            CreatedAt = start,
            StartsAt = start,
            EndsAt = start.AddDays(3),
        };
        segments.Times[anna] = 95_250;

        Assert.Equal(95_250, await scores.ScoreAsync(duel, anna, own: true, start, Cancel));
        Assert.Null(await scores.ScoreAsync(duel, boris, own: false, start, Cancel));
        Assert.Equal(
            [(anna, League.Bike, start, start.AddDays(3), false), (boris, League.Bike, start, start.AddDays(3), true)],
            segments.Asked);
        Assert.Equal(DuelSideKind.Challenger, DuelRules.Leader(95_250, null, DuelReferee.LowerIsBetter(DuelMetric.SegmentTime)));
    }

    // MARK: — вспомогательное

    private Task<HttpResponseMessage> ChallengeAsync(HttpClient client, Guid opponentId, int days, DuelMetric metric = DuelMetric.Distance) =>
        client.PostAsJsonAsync(
            "/duels", new CreateDuelRequest { OpponentId = opponentId, League = League.Run, Metric = metric, Days = days }, Json, Cancel);

    /// <summary>Друзья по коду (E14a).</summary>
    private async Task BefriendAsync(HttpClient first, HttpClient second)
    {
        var firstCode = (await first.GetFromJsonAsync<FriendsResponse>("/friends", Json, Cancel))!.MyCode;
        var secondCode = (await second.GetFromJsonAsync<FriendsResponse>("/friends", Json, Cancel))!.MyCode;
        await second.PostAsJsonAsync("/friends", new AddFriendRequest { Code = firstCode }, Json, Cancel);
        var back = await first.PostAsJsonAsync("/friends", new AddFriendRequest { Code = secondCode }, Json, Cancel);
        Assert.Equal(FriendStatus.Friend, (await Problems.BodyAsync<FriendResponse>(back, Cancel)).Status);
    }

    private async Task<Guid> IdOfAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<Gorodki.Api.Features.Me.MeResponse>("/me", Json, Cancel))!.Id;

    /// <summary>Судья дуэлей, как его зовёт Hangfire; <paramref name="notifier"/> — подставные «Входящие».</summary>
    private static async Task<int> RefereeAsync(ApiFactory api, Func<DuelReferee, Task<int>> stage, IDuelNotifier? notifier = null)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var referee = notifier is null
            ? services.GetRequiredService<DuelReferee>()
            : new DuelReferee(
                services.GetRequiredService<AppDbContext>(),
                services.GetRequiredService<DuelScores>(),
                services.GetRequiredService<IDuelStakes>(),
                notifier,
                api.Time,
                NullLogger<DuelReferee>.Instance);
        return await stage(referee);
    }

    private async Task<DuelEntity> StoredAsync(Guid id)
    {
        await using var db = database.CreateContext();
        return await db.Duels.AsNoTracking().SingleAsync(d => d.Id == id, Cancel);
    }

    /// <summary>Старый обработанный забег без точек с этого телефона — след устройства игрока.</summary>
    private async Task AddRunAsync(Guid userId, Guid deviceId)
    {
        await using var db = database.CreateContext();
        var startedAt = DateTimeOffset.UtcNow.AddDays(-20);
        db.Runs.Add(new RunEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            League = League.Run,
            Source = RunSource.Live,
            ConfigVersion = 1,
            StartedAt = startedAt,
            EndedAt = startedAt.AddHours(1),
            Status = RunStatus.Finished,
            CreatedAt = startedAt,
            DeviceId = deviceId,
            AppVersion = "0.1.0 (1)",
            MotionAuthorized = true,
            LastSeq = -1,
            FogStampedAt = startedAt.AddHours(1),
            VisitsProcessedAt = startedAt.AddHours(1),
            AcceptedMeters = 1_000,
            PointsPurgedAt = startedAt.AddDays(14),
        });
        await db.SaveChangesAsync(Cancel);
    }

    private sealed class RecordingNotifier : IDuelNotifier
    {
        public List<(Guid Duel, Guid Leader)> Leaders { get; } = [];

        public Task ChallengedAsync(AppDbContext db, DuelEntity duel, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task LeaderChangedAsync(AppDbContext db, DuelEntity duel, Guid leaderId, CancellationToken cancellationToken)
        {
            Leaders.Add((duel.Id, leaderId));
            return Task.CompletedTask;
        }

        public Task FinishedAsync(AppDbContext db, DuelEntity duel, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSegments : IDuelSegments
    {
        public Dictionary<Guid, long> Times { get; } = [];

        public List<(Guid Player, League League, DateTimeOffset From, DateTimeOffset To, bool PublicOnly)> Asked { get; } = [];

        public Task<bool> ExistsAsync(Guid segmentId, League league, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<long?> BestTimeMsAsync(
            Guid segmentId, Guid playerId, League league, DateTimeOffset from, DateTimeOffset to, bool publicOnly, CancellationToken cancellationToken)
        {
            Asked.Add((playerId, league, from, to, publicOnly));
            return Task.FromResult(Times.TryGetValue(playerId, out var time) ? time : (long?)null);
        }
    }
}
