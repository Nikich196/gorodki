using System.Security.Claims;
using System.Text.Json.Serialization;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Duels;
using Gorodki.Domain.Leagues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Duels;

/// <summary>Чем меряется дуэль (PLAN.md, §3.14).</summary>
public enum DuelMetric
{
    /// <summary>Захваченная площадь, м² («взятое» захватов, петля которых замкнута в срок дуэли).</summary>
    CapturedArea = 0,

    /// <summary>Открытая площадь тумана, м² (новые клетки забегов, начатых в срок дуэли).</summary>
    ExploredArea = 1,

    /// <summary>Дистанция, м (засчитанный путь забегов, начатых в срок дуэли).</summary>
    Distance = 2,

    /// <summary>Лучшее время на выбранном отрезке, мс (меньше — лучше).</summary>
    SegmentTime = 3,
}

/// <summary>Состояние дуэли.</summary>
public enum DuelStatus
{
    /// <summary>Вызов отправлен, соперник ещё не ответил.</summary>
    Pending = 0,

    /// <summary>Идёт; после конца срока — пока подводится итог (до 4 ч).</summary>
    Active = 1,

    /// <summary>Закончилась, итог подведён.</summary>
    Finished = 2,

    /// <summary>Соперник отказался или не ответил за сутки.</summary>
    Declined = 3,
}

/// <summary>Сторона дуэли.</summary>
/// <param name="PlayerId">Номер игрока; <see cref="Guid.Empty"/> — аккаунт соперника удалён.</param>
/// <param name="Name">
/// Ник — если игрок согласился его показывать или это ты сам; иначе «Игрок #1234»; удалённый аккаунт — «Игрок удалён».
/// </param>
/// <param name="Score">
/// Счёт в единицах метрики (м², м), округлён до 0,1; у <c>segmentTime</c> — лучшее время, мс; <c>null</c> — ещё нет
/// результата (вызов не принят, у отрезка нет прохода). Свой — сразу, как в итоге заявки; соперника — как его видит
/// посторонний (граница публичности). У законченной — окончательный.
/// </param>
/// <param name="Staked">Поставил фишку из Рюкзака.</param>
public sealed record DuelSide(Guid PlayerId, string Name, short ColorIndex, double? Score, bool Staked);

/// <summary>Дуэль глазами спрашивающего.</summary>
/// <param name="Incoming">
/// Вызов пришёл мне: я — вызванная сторона. У <c>pending</c> — только вызванному показывать «Принять / Отклонить»; у
/// остальных — кто кого вызвал, по факту.
/// </param>
/// <param name="SegmentId">Отрезок — у метрики <c>segmentTime</c>, иначе <c>null</c>.</param>
/// <param name="Days">Срок: 1, 3 или 7 дней.</param>
/// <param name="StartsAtMs">Начало (мс Unix) — когда соперник принял вызов; <c>null</c> — ещё не принят.</param>
/// <param name="EndsAtMs">Конец (мс Unix); <c>null</c> — ещё не принят.</param>
/// <param name="WinnerId">
/// Победитель; <c>null</c> — дуэль не закончена, ничья или снята (оба аккаунта бегали с одного телефона, соперник удалил
/// аккаунт). Стёртый аккаунт — <see cref="Guid.Empty"/>, как номер его стороны.
/// </param>
public sealed record DuelResponse(
    Guid Id,
    DuelStatus Status,
    bool Incoming,
    League League,
    DuelMetric Metric,
    Guid? SegmentId,
    int Days,
    DuelSide Me,
    DuelSide Opponent,
    long? StartsAtMs,
    long? EndsAtMs,
    Guid? WinnerId);

/// <summary>Свои дуэли: вызовы, идущие и недавно законченные.</summary>
public sealed record DuelsResponse(IReadOnlyList<DuelResponse> Duels);

/// <summary>Вызвать друга на дуэль.</summary>
public sealed record CreateDuelRequest
{
    /// <summary>Соперник — взаимный друг в этой же лиге.</summary>
    [JsonRequired]
    public required Guid OpponentId { get; init; }

    [JsonRequired]
    public required League League { get; init; }

    [JsonRequired]
    public required DuelMetric Metric { get; init; }

    /// <summary>1, 3 или 7.</summary>
    [JsonRequired]
    public required int Days { get; init; }

    /// <summary>Отрезок — обязателен у метрики <c>segmentTime</c>, иначе не нужен.</summary>
    public Guid? SegmentId { get; init; }

    /// <summary>Фишка из своего Рюкзака на кон (по желанию); победитель забирает обе.</summary>
    public Guid? StakeItemId { get; init; }
}

/// <summary>Принять вызов.</summary>
public sealed record AcceptDuelRequest
{
    /// <summary>Своя фишка на кон (по желанию).</summary>
    public Guid? StakeItemId { get; init; }
}

/// <summary>
/// Дуэли 1×1 (PLAN.md, §3.14): только взаимные друзья одной лиги, срок 1, 3 или 7 дней, метрика — захваченная площадь,
/// открытая площадь, дистанция или лучшее время на отрезке; ставка — по фишке из Рюкзака (значки «Коллекции» на кон не
/// ставятся). Защита: «одно устройство — один аккаунт», аккаунт старше 48 ч, не больше 2 активных дуэлей, одна пара — не
/// чаще раза в неделю. Задача E17 (#147, docs/guides/egor-server.md, раздел 7) — сделано Claude 15.10; устройство —
/// docs/architecture/duels.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>Жизнь дуэли.</b> Вызов (<c>pending</c>) ждёт ответа сутки (<see cref="DuelRules.PendingLifetime"/>), потом считается
/// отклонённым. Принят — <c>active</c>: срок идёт с момента принятия. Конец срока — ещё не итог: итог подводит задача
/// <see cref="DuelReferee"/> через 4 ч, когда дойдут поздние данные; тогда <c>finished</c> и <c>winnerId</c> (ничья и
/// снятая дуэль — <c>null</c>). Отказ или истёкший вызов — <c>declined</c>.
/// </para>
/// <para>
/// <b>Лимиты — под блокировкой обоих игроков</b> (<see cref="DuelReferee.LockPlayersAsync"/>, пространство 9): «не больше
/// 2 активных» считает идущие дуэли игрока и его же ещё не отвеченные вызовы (иначе можно разослать десять вызовов и
/// получить десять дуэлей), «пара — раз в неделю» — ждущий вызов пары или дуэль пары, начатую меньше 7 дней назад или ещё
/// идущую; отказ и истёкший вызов в неё не идут.
/// </para>
/// <para>
/// <b>Живой счёт — по границе публичности</b> (<see cref="DuelScores"/>): свой — сразу, соперника — как его видит
/// посторонний; итог и смена лидера — только по публичному счёту обоих. <b>Чужое — «нет такого»:</b> дуэль ищется с
/// условием «спрашивающий — сторона» в том же запросе; принять и отклонить может только вызванный.
/// </para>
/// </remarks>
public static class DuelEndpoints
{
    /// <summary>Активных дуэлей у игрока — не больше (§3.14).</summary>
    public const int MaxActive = DuelRules.MaxActive;

    /// <summary>Имя стороны, чей аккаунт удалён или удаляется.</summary>
    public const string DeletedName = "Игрок удалён";

    public static IEndpointRouteBuilder MapDuelEndpoints(this IEndpointRouteBuilder app)
    {
        var duels = app.MapGroup("/duels").WithTags("Дуэли").RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        duels.MapGet("", ListDuels)
            .WithName("listDuels")
            .WithSummary("Свои дуэли: вызовы, идущие и законченные за 30 дней");
        duels.MapPost("", CreateDuel)
            .WithName("createDuel")
            .WithSummary("Вызвать друга на дуэль")
            .WithDescription(
                "Не взаимный друг — 404 duel_opponent_not_found; неверные срок, лига или метрика, нет отрезка у segmentTime — 400 "
                + "duel_invalid; аккаунт моложе 48 ч — 409 duel_account_too_new; уже 2 активных — 409 duel_limit; с этим "
                + "соперником уже была дуэль на этой неделе — 409 duel_pair_week; фишки на кон нет или она активирована — 409 "
                + "duel_stake_invalid.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        duels.MapGet("/{id:guid}", GetDuel)
            .WithName("getDuel")
            .WithSummary("Дуэль с живым счётом (счёт — по границе публичности, как видит посторонний)")
            .WithDescription("Не своя или нет такой — 404 duel_not_found.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        duels.MapPost("/{id:guid}/accept", AcceptDuel)
            .WithName("acceptDuel")
            .WithSummary("Принять вызов (по желанию — со своей фишкой на кон)")
            .WithDescription(
                "Вызов не тебе или уже не ждёт ответа — 404 duel_not_found; лимиты — как при вызове (409 duel_limit, "
                + "duel_account_too_new, duel_stake_invalid).")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        duels.MapPost("/{id:guid}/decline", DeclineDuel)
            .WithName("declineDuel")
            .WithSummary("Отказаться от вызова")
            .WithDescription("Вызов не тебе или уже не ждёт ответа — 404 duel_not_found.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<Ok<DuelsResponse>> ListDuels(
        ClaimsPrincipal principal, AppDbContext db, DuelScores scores, TimeProvider time, CancellationToken cancellationToken)
    {
        var me = principal.UserId();
        var now = time.GetUtcNow();
        var since = now - DuelReferee.RecentlyFinished;
        var duels = await db.Duels.AsNoTracking()
            .Where(d => (d.ChallengerId == me || d.OpponentId == me)
                && (d.Status == DuelStatus.Pending || d.Status == DuelStatus.Active || d.FinishedAt >= since))
            .ToListAsync(cancellationToken);

        // Сначала ждущие ответа, потом идущие, потом законченные; внутри — новые выше.
        var ordered = duels
            .OrderBy(d => StatusOf(d, now) switch { DuelStatus.Pending => 0, DuelStatus.Active => 1, _ => 2 })
            .ThenByDescending(d => d.FinishedAt ?? d.StartsAt ?? d.CreatedAt)
            .ThenBy(d => d.Id)
            .ToList();
        return TypedResults.Ok(new DuelsResponse(me is { } id ? await ToResponsesAsync(db, scores, ordered, id, now, cancellationToken) : []));
    }

    private static async Task<Results<Created<DuelResponse>, ProblemHttpResult>> CreateDuel(
        CreateDuelRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        DuelScores scores,
        IDuelSegments segments,
        IDuelStakes stakes,
        IDuelNotifier notifier,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } me)
        {
            return Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Нужен вход.");
        }

        // Условия — до базы: срок, лига, метрика и отрезок ровно у segmentTime.
        if (!DuelRules.IsAllowedDays(request.Days) || !Enum.IsDefined(request.League) || !Enum.IsDefined(request.Metric)
            || (request.Metric == DuelMetric.SegmentTime) != request.SegmentId.HasValue)
        {
            return Invalid("Срок — 1, 3 или 7 дней; отрезок — только и обязательно у метрики «лучшее время на отрезке».");
        }

        if (request.SegmentId is { } segment && !await segments.ExistsAsync(segment, request.League, cancellationToken))
        {
            return Invalid("Такого отрезка нет.");
        }

        var opponentId = request.OpponentId;
        if (opponentId == me)
        {
            return OpponentNotFound();
        }

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DuelReferee.LockPlayersAsync(db, me, opponentId, cancellationToken);

        // Всё ниже — под блокировкой обоих: второй вызов в ту же секунду увидит первый.
        var ages = await AccountsAsync(db, me, opponentId, cancellationToken);
        if (!ages.TryGetValue(me, out var myCreatedAt))
        {
            return Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Аккаунт удаляется.");
        }

        if (!ages.TryGetValue(opponentId, out var theirCreatedAt) || !await MutualFriendsAsync(db, me, opponentId, cancellationToken))
        {
            return OpponentNotFound();
        }

        if (!DuelRules.IsOldEnough(myCreatedAt, now))
        {
            return TooNew("Вызывать на дуэль можно через 48 часов после регистрации.");
        }

        if (!DuelRules.IsOldEnough(theirCreatedAt, now))
        {
            return TooNew("Аккаунту соперника ещё нет 48 часов.");
        }

        if (await DuelReferee.SharedDeviceAsync(db, me, opponentId, cancellationToken))
        {
            // «Одно устройство — один аккаунт» (§3.14): свой второй аккаунт — не соперник. Кода в контракте под это нет —
            // «соперник не найден» (docs/architecture/duels.md, «Решено без плана»).
            return Problem(
                StatusCodes.Status404NotFound, "duel_opponent_not_found", "С аккаунтом, который бегал с твоего телефона, дуэли нет.");
        }

        if (await SlotsTakenAsync(db, me, now, cancellationToken) >= DuelRules.MaxActive)
        {
            return Limit();
        }

        if (await PairBusyAsync(db, me, opponentId, now, cancellationToken))
        {
            return Problem(StatusCodes.Status409Conflict, "duel_pair_week", "С этим соперником дуэль уже была на этой неделе.");
        }

        var duel = new DuelEntity
        {
            Id = Guid.NewGuid(), // не v7: в нём было бы время вызова
            ChallengerId = me,
            OpponentId = opponentId,
            League = request.League,
            Metric = request.Metric,
            SegmentId = request.SegmentId,
            Days = (short)request.Days,
            Status = DuelStatus.Pending,
            CreatedAt = now,
            ChallengerStakeItemId = request.StakeItemId,
        };
        if (request.StakeItemId is { } item && !await stakes.TryStakeAsync(db, me, item, duel.Id, cancellationToken))
        {
            return StakeInvalid();
        }

        db.Duels.Add(duel);
        await notifier.ChallengedAsync(db, duel, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var response = (await ToResponsesAsync(db, scores, [duel], me, now, cancellationToken))[0];
        return TypedResults.Created($"/duels/{duel.Id}", response);
    }

    private static async Task<Results<Ok<DuelResponse>, ProblemHttpResult>> GetDuel(
        Guid id, ClaimsPrincipal principal, AppDbContext db, DuelScores scores, TimeProvider time, CancellationToken cancellationToken)
    {
        // Условие «спрашивающий — сторона» — в том же запросе (egor-server.md, раздел 4, п. 5): чужая — «нет такой».
        var me = principal.UserId();
        var duel = await db.Duels.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == id && (d.ChallengerId == me || d.OpponentId == me), cancellationToken);
        return duel is null || me is not { } viewer
            ? NotFound()
            : TypedResults.Ok((await ToResponsesAsync(db, scores, [duel], viewer, time.GetUtcNow(), cancellationToken))[0]);
    }

    private static async Task<Results<Ok<DuelResponse>, ProblemHttpResult>> AcceptDuel(
        Guid id,
        AcceptDuelRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        DuelScores scores,
        IDuelStakes stakes,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        // Вызов, где спрашивающий — вызванный, и он ещё ждёт ответа; кто вызвал — чтобы взять блокировки обоих по порядку.
        var me = principal.UserId();
        var now = time.GetUtcNow();
        var cutoff = now - DuelRules.PendingLifetime;
        var challengerId = await db.Duels.AsNoTracking()
            .Where(d => d.Id == id && d.OpponentId == me && d.Status == DuelStatus.Pending && d.CreatedAt > cutoff)
            .Select(d => d.ChallengerId)
            .SingleOrDefaultAsync(cancellationToken);
        if (me is not { } accepter || challengerId is not { } challenger)
        {
            return NotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DuelReferee.LockPlayersAsync(db, accepter, challenger, cancellationToken);
        var duel = await db.Duels.SingleOrDefaultAsync(
            d => d.Id == id && d.OpponentId == accepter && d.Status == DuelStatus.Pending && d.CreatedAt > cutoff, cancellationToken);
        var ages = await AccountsAsync(db, accepter, challenger, cancellationToken);
        if (duel is null || !ages.TryGetValue(accepter, out var myCreatedAt) || !ages.ContainsKey(challenger)
            || !await MutualFriendsAsync(db, accepter, challenger, cancellationToken))
        {
            return NotFound(); // приняли, отклонили, истёк, или вы больше не друзья (§3.14: «только взаимные друзья»)
        }

        if (!DuelRules.IsOldEnough(myCreatedAt, now))
        {
            return TooNew("Принимать вызовы можно через 48 часов после регистрации.");
        }

        if (await DuelReferee.SharedDeviceAsync(db, accepter, challenger, cancellationToken))
        {
            return Problem(StatusCodes.Status404NotFound, "duel_not_found", "Этот вызов с аккаунта, который бегал с твоего телефона.");
        }

        if (await SlotsTakenAsync(db, accepter, now, cancellationToken) >= DuelRules.MaxActive)
        {
            return Limit();
        }

        if (request.StakeItemId is { } item && !await stakes.TryStakeAsync(db, accepter, item, duel.Id, cancellationToken))
        {
            return StakeInvalid();
        }

        duel.Status = DuelStatus.Active;
        duel.StartsAt = now;
        duel.EndsAt = now.AddDays(duel.Days);
        duel.OpponentStakeItemId = request.StakeItemId;
        duel.Leader = null; // 0 : 0
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await ToResponsesAsync(db, scores, [duel], accepter, now, cancellationToken))[0]);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeclineDuel(
        Guid id, ClaimsPrincipal principal, AppDbContext db, IDuelStakes stakes, TimeProvider time, CancellationToken cancellationToken)
    {
        // Только вызванный и только ждущий ответа вызов — условие в том же запросе; ставка вызвавшего — назад.
        var me = principal.UserId();
        var now = time.GetUtcNow();
        var cutoff = now - DuelRules.PendingLifetime;
        return await DuelReferee.DeclineAsync(db, stakes, d => d.Id == id && d.OpponentId == me && d.CreatedAt > cutoff, now, cancellationToken)
            ? TypedResults.NoContent()
            : NotFound();
    }

    // MARK: — правила под блокировкой

    /// <summary>Когда заведены аккаунты этих игроков; удаляемых (человек попросил забыть) в ответе нет.</summary>
    private static Task<Dictionary<Guid, DateTimeOffset>> AccountsAsync(AppDbContext db, Guid first, Guid second, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking()
            .Where(u => (u.Id == first || u.Id == second) && u.DeletionRequestedAt == null)
            .ToDictionaryAsync(u => u.Id, u => u.CreatedAt, cancellationToken);

    /// <summary>Взаимные друзья (§3.8): строки «добавил» в обе стороны. Блокировка стирает их, так что её проверять не нужно.</summary>
    private static async Task<bool> MutualFriendsAsync(AppDbContext db, Guid first, Guid second, CancellationToken cancellationToken) =>
        await db.FriendLinks.CountAsync(
            f => (f.UserId == first && f.FriendId == second) || (f.UserId == second && f.FriendId == first), cancellationToken) == 2;

    /// <summary>
    /// Сколько мест из <see cref="DuelRules.MaxActive"/> занято: идущие дуэли игрока (с любой стороны, и те, что ждут итога) и
    /// его же вызовы, которые ещё ждут ответа. Пришедшие ему вызовы место не занимают — он их не выбирал.
    /// </summary>
    private static Task<int> SlotsTakenAsync(AppDbContext db, Guid player, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - DuelRules.PendingLifetime;
        return db.Duels.CountAsync(
            d => (d.Status == DuelStatus.Active && (d.ChallengerId == player || d.OpponentId == player))
                || (d.Status == DuelStatus.Pending && d.ChallengerId == player && d.CreatedAt > cutoff),
            cancellationToken);
    }

    /// <summary>
    /// «Одна пара — не чаще раза в неделю» (§3.14): у пары (в любую сторону) есть ждущий ответа вызов, идущая дуэль или дуэль,
    /// начатая меньше 7 дней назад. Отказ и истёкший вызов не в счёт — дуэли не было.
    /// </summary>
    private static Task<bool> PairBusyAsync(AppDbContext db, Guid first, Guid second, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - DuelRules.PendingLifetime;
        var recent = now - DuelRules.PairCooldown;
        return db.Duels.AnyAsync(
            d => ((d.ChallengerId == first && d.OpponentId == second) || (d.ChallengerId == second && d.OpponentId == first))
                && ((d.Status == DuelStatus.Pending && d.CreatedAt > cutoff) || d.Status == DuelStatus.Active || d.StartsAt > recent),
            cancellationToken);
    }

    // MARK: — ответ

    /// <summary>Статус для ответа: вызов, который не дождался ответа за сутки, — уже отклонён, даже если задача его ещё не закрыла.</summary>
    private static DuelStatus StatusOf(DuelEntity duel, DateTimeOffset now) =>
        duel.Status == DuelStatus.Pending && duel.CreatedAt <= now - DuelRules.PendingLifetime ? DuelStatus.Declined : duel.Status;

    /// <summary>
    /// Дуэли глазами <paramref name="me"/>: «я» — всегда он. Имена — одним запросом, по правилу карточки игрока (#115): свой ник
    /// всегда, чужой — с согласия, иначе «Игрок #1234»; удалённый аккаунт — <see cref="DeletedName"/>. Счёт идущей дуэли —
    /// свой сразу, соперника — по границе публичности (<see cref="DuelScores"/>); у законченной — окончательный.
    /// </summary>
    private static async Task<List<DuelResponse>> ToResponsesAsync(
        AppDbContext db, DuelScores scores, IReadOnlyList<DuelEntity> duels, Guid me, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ids = duels.SelectMany(d => new[] { d.ChallengerId, d.OpponentId }).OfType<Guid>().Distinct().ToList();
        var players = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.ColorIndex, u.PublicProfile, Deleting = u.DeletionRequestedAt != null })
            .ToDictionaryAsync(u => u.Id, cancellationToken);
        var result = new List<DuelResponse>(duels.Count);
        foreach (var duel in duels)
        {
            var incoming = duel.OpponentId == me;
            var (myId, theirId) = incoming ? (duel.OpponentId, duel.ChallengerId) : (duel.ChallengerId, duel.OpponentId);
            var (myStake, theirStake) = incoming
                ? (duel.OpponentStakeItemId, duel.ChallengerStakeItemId)
                : (duel.ChallengerStakeItemId, duel.OpponentStakeItemId);
            var status = StatusOf(duel, now);
            var (myScore, theirScore) = status switch
            {
                DuelStatus.Active => (
                    await scores.ScoreAsync(duel, me, own: true, now, cancellationToken),
                    theirId is { } other ? await scores.ScoreAsync(duel, other, own: false, now, cancellationToken) : null),
                DuelStatus.Finished => incoming ? (duel.OpponentScore, duel.ChallengerScore) : (duel.ChallengerScore, duel.OpponentScore),
                _ => ((double?)null, (double?)null),
            };

            DuelSide Side(Guid? id, double? score, Guid? stake, bool mine) =>
                id is { } playerId && players.TryGetValue(playerId, out var player) && (mine || !player.Deleting)
                    ? new DuelSide(
                        playerId,
                        mine || player.PublicProfile ? player.DisplayName : LeaderboardEndpoints.Pseudonym(playerId),
                        player.ColorIndex,
                        score,
                        stake is not null)
                    : new DuelSide(id ?? Guid.Empty, DeletedName, 0, score, stake is not null);

            result.Add(new DuelResponse(
                duel.Id,
                status,
                incoming,
                duel.League,
                duel.Metric,
                duel.SegmentId,
                duel.Days,
                Side(myId, myScore, myStake, mine: true),
                Side(theirId, theirScore, theirStake, mine: false),
                duel.StartsAt?.ToUnixTimeMilliseconds(),
                duel.EndsAt?.ToUnixTimeMilliseconds(),
                DuelReferee.WinnerOf(duel)));
        }

        return result;
    }

    // MARK: — ошибки

    private static ProblemHttpResult NotFound() =>
        Problem(StatusCodes.Status404NotFound, "duel_not_found", "Такой дуэли нет или вызов уже не ждёт ответа.");

    private static ProblemHttpResult OpponentNotFound() =>
        Problem(StatusCodes.Status404NotFound, "duel_opponent_not_found", "Вызвать можно только взаимного друга.");

    private static ProblemHttpResult Invalid(string title) => Problem(StatusCodes.Status400BadRequest, "duel_invalid", title);

    private static ProblemHttpResult TooNew(string title) => Problem(StatusCodes.Status409Conflict, "duel_account_too_new", title);

    private static ProblemHttpResult Limit() =>
        Problem(StatusCodes.Status409Conflict, "duel_limit", $"Дуэлей сразу — не больше {DuelRules.MaxActive}, считая свои вызовы без ответа.");

    private static ProblemHttpResult StakeInvalid() =>
        Problem(StatusCodes.Status409Conflict, "duel_stake_invalid", "Эту фишку поставить нельзя: её нет в Рюкзаке, она активирована или уже на кону.");

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
