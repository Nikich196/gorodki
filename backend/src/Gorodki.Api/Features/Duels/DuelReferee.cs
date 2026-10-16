using System.Linq.Expressions;
using Gorodki.Api.Features.Scoring;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Duels;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Duels;

/// <summary>
/// Судья дуэлей (PLAN.md, §3.14) — задача Hangfire <c>duels-referee</c> раз в 15 минут (docs/architecture/jobs.md): истёкшие
/// вызовы закрывает как отклонённые, у идущих дуэлей следит за лидером (уведомление «смена лидера»), закончившимся
/// подводит итог — счёт, победитель, серия побед, ставки. Каждая стадия и каждая дуэль — своя транзакция: ошибка одной
/// не мешает остальным (они пишутся в лог, следующий запуск попробует снова).
/// </summary>
/// <remarks>
/// <para>
/// <b>Итог — не в конце срока, а через <see cref="SettleDelay"/> после него</b> (4 ч, как закрытие сезона,
/// <see cref="ScoreBook.CloseGrace"/>): петля последней минуты применяется до 3 ч спустя и видна через 20–25 минут,
/// дистанция и туман забега, начатого перед концом, — после его конца и подсчёта визитов. До итога дуэль остаётся
/// <c>active</c> с <c>endsAtMs</c> в прошлом: телефон пишет «итог подводится». Итог — по счёту, который видят оба
/// (<see cref="DuelScores"/>, взгляд постороннего), и после подведения не меняется: счёт замораживается в строке дуэли.
/// </para>
/// <para>
/// <b>Идемпотентность.</b> Итог — под блокировками обоих игроков (пространство <see cref="LockSpace"/>, как вызов и
/// принятие) с повторным чтением статуса: второй запуск (повтор Hangfire, второй экземпляр сервера) находит дуэль уже
/// законченной и ничего не делает. Ставки уходят в той же транзакции. Закрытие вызова и смена лидера — условным
/// <c>UPDATE</c> по прежнему значению: повтор не закроет дважды и не уведомит дважды.
/// </para>
/// </remarks>
public sealed class DuelReferee(
    AppDbContext db, DuelScores scores, IDuelStakes stakes, IDuelNotifier notifier, TimeProvider time, ILogger<DuelReferee> logger)
{
    /// <summary>Пространство блокировок «игрок в дуэлях» (docs/architecture/jobs.md, «Пространства блокировок»).</summary>
    public const int LockSpace = 9;

    /// <summary>Через сколько после конца дуэли подводится итог — запас на поздние данные, как у закрытия сезона.</summary>
    public static readonly TimeSpan SettleDelay = ScoreBook.CloseGrace;

    /// <summary>Сколько законченных дуэлей показывать в списке (§3.14: «вызовы, идущие и законченные за 30 дней»).</summary>
    public static readonly TimeSpan RecentlyFinished = TimeSpan.FromDays(30);

    private const int Batch = 50;

    /// <summary>Задача по расписанию: все три стадии. Возвращает, сколько дуэлей изменилось.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken) =>
        await ExpirePendingAsync(cancellationToken) + await TrackLeadersAsync(cancellationToken) + await SettleDueAsync(cancellationToken);

    /// <summary>Вызовы, которые ждут ответа дольше <see cref="DuelRules.PendingLifetime"/>, — отклонены (ставка — назад).</summary>
    public async Task<int> ExpirePendingAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var cutoff = now - DuelRules.PendingLifetime;
        var due = await db.Duels.AsNoTracking()
            .Where(d => d.Status == DuelStatus.Pending && d.CreatedAt <= cutoff)
            .OrderBy(d => d.CreatedAt)
            .Select(d => d.Id)
            .Take(Batch)
            .ToListAsync(cancellationToken);
        var expired = 0;
        foreach (var id in due)
        {
            expired += await SafelyAsync(id, () => DeclineAsync(db, stakes, d => d.Id == id && d.CreatedAt <= cutoff, now, cancellationToken)) ? 1 : 0;
        }

        return expired;
    }

    /// <summary>
    /// Лидер идущих дуэлей по счёту, который видят оба: сменился — запомнить и уведомить (§3.14). Только до конца срока: после
    /// него поздние данные двигают уже итог, о нём — своё уведомление.
    /// </summary>
    public async Task<int> TrackLeadersAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var live = await db.Duels.AsNoTracking()
            .Where(d => d.Status == DuelStatus.Active && d.EndsAt > now)
            .OrderBy(d => d.EndsAt)
            .ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var duel in live)
        {
            changed += await SafelyAsync(duel.Id, () => TrackLeaderAsync(duel, now, cancellationToken)) ? 1 : 0;
        }

        return changed;
    }

    /// <summary>Итог дуэлей, у которых с конца срока прошло <see cref="SettleDelay"/>.</summary>
    public async Task<int> SettleDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var endedBefore = now - SettleDelay;
        var due = await db.Duels.AsNoTracking()
            .Where(d => d.Status == DuelStatus.Active && d.EndsAt <= endedBefore)
            .OrderBy(d => d.EndsAt)
            .Select(d => d.Id)
            .Take(Batch)
            .ToListAsync(cancellationToken);
        var settled = 0;
        foreach (var id in due)
        {
            settled += await SafelyAsync(id, () => SettleAsync(id, now, cancellationToken)) ? 1 : 0;
        }

        return settled;
    }

    private async Task<bool> TrackLeaderAsync(DuelEntity duel, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (challenger, opponent) = (duel.ChallengerId!.Value, duel.OpponentId!.Value);
        var leader = DuelRules.Leader(
            await scores.ScoreAsync(duel, challenger, own: false, now, cancellationToken),
            await scores.ScoreAsync(duel, opponent, own: false, now, cancellationToken),
            LowerIsBetter(duel.Metric));
        if (leader == duel.Leader)
        {
            return false;
        }

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var previous = duel.Leader;
        var updated = await db.Duels
            .Where(d => d.Id == duel.Id && d.Status == DuelStatus.Active && d.Leader == previous)
            .ExecuteUpdateAsync(set => set.SetProperty(d => d.Leader, leader), cancellationToken);
        if (updated == 0)
        {
            return false; // другой запуск успел раньше
        }

        // Уведомляем, когда вперёд вышел кто-то (не о «стало поровну»): было поровну или впереди был соперник.
        if (leader is { } side)
        {
            await notifier.LeaderChangedAsync(db, duel, side == DuelSideKind.Challenger ? challenger : opponent, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Итог одной дуэли под блокировками обоих игроков. False — дуэль уже не идёт (итог подвёл другой запуск).</summary>
    private async Task<bool> SettleAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var sides = await db.Duels.AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => new { d.ChallengerId, d.OpponentId })
            .SingleOrDefaultAsync(cancellationToken);
        if (sides is not { ChallengerId: { } challenger, OpponentId: { } opponent })
        {
            return false;
        }

        await LockPlayersAsync(db, challenger, opponent, cancellationToken);
        var duel = await db.Duels.SingleOrDefaultAsync(d => d.Id == id && d.Status == DuelStatus.Active, cancellationToken);
        if (duel is null)
        {
            return false;
        }

        // Счёт — как его видят оба; дуэль аккаунтов с одного телефона снимается, даже если общий телефон появился уже по ходу.
        duel.ChallengerScore = await scores.ScoreAsync(duel, challenger, own: false, now, cancellationToken);
        duel.OpponentScore = await scores.ScoreAsync(duel, opponent, own: false, now, cancellationToken);
        duel.Outcome = await SharedDeviceAsync(db, challenger, opponent, cancellationToken)
            ? DuelOutcome.Cancelled
            : DuelRules.Decide(duel.ChallengerScore, duel.OpponentScore, LowerIsBetter(duel.Metric));
        duel.Leader = DuelRules.Leader(duel.ChallengerScore, duel.OpponentScore, LowerIsBetter(duel.Metric));
        duel.Status = DuelStatus.Finished;
        duel.FinishedAt = now;
        var winnerId = WinnerOf(duel);
        duel.WinnerStreak = winnerId is { } winner ? (short)(await DuelStreaks.WinStreakAsync(db, winner, cancellationToken) + 1) : null;

        await stakes.SettleAsync(db, duel, winnerId, cancellationToken);
        await notifier.FinishedAsync(db, duel, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Дуэль {DuelId}: итог {Outcome}", id, duel.Outcome);
        return true;
    }

    private async Task<bool> SafelyAsync(Guid id, Func<Task<bool>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Дуэль {DuelId} не обработана — следующий запуск попробует снова", id);
            return false;
        }
    }

    // MARK: — общее для адресов, задачи и удаления аккаунта

    /// <summary>У <c>segmentTime</c> меньше — лучше, у площадей и дистанции — больше.</summary>
    public static bool LowerIsBetter(DuelMetric metric) => metric == DuelMetric.SegmentTime;

    /// <summary>
    /// Победитель законченной дуэли; <c>null</c> — ничья, дуэль снята или ещё не закончена. Если аккаунт победителя уже
    /// стёрт — <see cref="Guid.Empty"/>, как номер его стороны в ответе.
    /// </summary>
    public static Guid? WinnerOf(DuelEntity duel) => duel.Status != DuelStatus.Finished ? null : duel.Outcome switch
    {
        DuelOutcome.ChallengerWon => duel.ChallengerId ?? Guid.Empty,
        DuelOutcome.OpponentWon => duel.OpponentId ?? Guid.Empty,
        _ => null,
    };

    /// <summary>
    /// Оба игрока — под блокировку PostgreSQL (пространство <see cref="LockSpace"/>) до конца транзакции, по порядку номеров:
    /// вызов, принятие и итог с одним игроком идут по очереди (лимит активных и «пара — раз в неделю» считаются под ней), а
    /// встречные блокировки не ждут друг друга.
    /// </summary>
    public static async Task LockPlayersAsync(AppDbContext db, Guid first, Guid second, CancellationToken cancellationToken)
    {
        foreach (var key in new[] { first.ToString(), second.ToString() }.Distinct().Order(StringComparer.Ordinal))
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockSpace}, hashtext({key}))", cancellationToken);
        }
    }

    /// <summary>
    /// Правило «одно устройство — один аккаунт» (§3.14, как <c>device_shared</c> у захвата, §3.3): бегал ли хоть раз один и тот
    /// же телефон (<c>runs.device_id</c> — установка из Keychain) за обоих игроков.
    /// </summary>
    public static Task<bool> SharedDeviceAsync(AppDbContext db, Guid first, Guid second, CancellationToken cancellationToken) =>
        db.Runs.Where(r => r.UserId == first).Select(r => r.DeviceId)
            .Intersect(db.Runs.Where(r => r.UserId == second).Select(r => r.DeviceId))
            .AnyAsync(cancellationToken);

    /// <summary>
    /// Ждущий ответа вызов, подходящий под <paramref name="which"/>, — отклонён, и ставка вызвавшего возвращена, одной
    /// транзакцией. False — такого ждущего вызова нет (или его успели принять, отклонить, закрыть).
    /// </summary>
    public static async Task<bool> DeclineAsync(
        AppDbContext db, IDuelStakes stakes, Expression<Func<DuelEntity, bool>> which, DateTimeOffset now, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var duel = await db.Duels.Where(d => d.Status == DuelStatus.Pending).Where(which).SingleOrDefaultAsync(cancellationToken);
        if (duel is null)
        {
            return false;
        }

        duel.Status = DuelStatus.Declined;
        duel.FinishedAt = now;
        var updated = await db.Duels
            .Where(d => d.Id == duel.Id && d.Status == DuelStatus.Pending)
            .ExecuteUpdateAsync(set => set.SetProperty(d => d.Status, DuelStatus.Declined).SetProperty(d => d.FinishedAt, now), cancellationToken);
        if (updated == 0)
        {
            return false;
        }

        db.Entry(duel).State = EntityState.Unchanged; // уже записано условным UPDATE
        await stakes.SettleAsync(db, duel, winnerId: null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Удаление аккаунта (закон 99-З): ждущие вызовы игрока стираются, идущие дуэли снимаются (исход «снята», ставки — назад,
    /// второму — уведомление), законченные остаются у соперника — номер игрока в них сотрёт база при удалении строки игрока
    /// (<c>ON DELETE SET NULL</c>). Зовётся в транзакции удаления, под блокировкой игрока в дуэлях — итог, который подводит
    /// задача в эту секунду, не разойдётся с удалением.
    /// </summary>
    public static async Task ForgetPlayerAsync(
        AppDbContext db, IDuelStakes stakes, IDuelNotifier notifier, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var key = userId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockSpace}, hashtext({key}))", cancellationToken);
        var open = await db.Duels
            .Where(d => (d.ChallengerId == userId || d.OpponentId == userId)
                && (d.Status == DuelStatus.Pending || d.Status == DuelStatus.Active))
            .ToListAsync(cancellationToken);
        foreach (var duel in open)
        {
            await stakes.SettleAsync(db, duel, winnerId: null, cancellationToken);
            if (duel.Status == DuelStatus.Pending)
            {
                db.Duels.Remove(duel);
                continue;
            }

            duel.Status = DuelStatus.Finished;
            duel.Outcome = DuelOutcome.Cancelled;
            duel.FinishedAt = now;
            await notifier.FinishedAsync(db, duel, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Серия побед и дуэльный значок (§3.7, §3.14: «редкость растёт с серией побед — бронза → серебро → золото»). Значок — за
/// каждую победу, его редкость — по серии вместе с ней (<see cref="DuelEntity.WinnerStreak"/>). В контракте полей значка
/// пока нет: показать — в профиле и карточке игрока (лучший значок и текущая серия) и у законченной дуэли в списке
/// (docs/architecture/duels.md, «Значок»).
/// </summary>
public static class DuelStreaks
{
    /// <summary>Текущая серия побед игрока по его законченным дуэлям (ничьи и снятые серию не рвут, <see cref="DuelRules.WinStreak"/>).</summary>
    public static async Task<int> WinStreakAsync(AppDbContext db, Guid playerId, CancellationToken cancellationToken)
    {
        var finished = await db.Duels.AsNoTracking()
            .Where(d => d.Status == DuelStatus.Finished && (d.ChallengerId == playerId || d.OpponentId == playerId))
            .OrderByDescending(d => d.FinishedAt)
            .ThenByDescending(d => d.Id)
            .Select(d => new { d.ChallengerId, d.Outcome })
            .ToListAsync(cancellationToken);
        return DuelRules.WinStreak(finished.Select(d => d.Outcome switch
        {
            DuelOutcome.ChallengerWon => d.ChallengerId == playerId,
            DuelOutcome.OpponentWon => d.ChallengerId != playerId,
            _ => (bool?)null,
        }));
    }

    /// <summary>Текущая серия побед и редкость значка за последнюю победу в ней (<see cref="DuelBadge.None"/> — серии нет).</summary>
    public static async Task<(int Streak, DuelBadge Badge)> CurrentAsync(AppDbContext db, Guid playerId, CancellationToken cancellationToken)
    {
        var streak = await WinStreakAsync(db, playerId, cancellationToken);
        return (streak, DuelRules.BadgeFor(streak));
    }
}
