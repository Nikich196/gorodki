using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;

namespace Gorodki.Api.Features.Duels;

/// <summary>
/// Ставка в дуэли (PLAN.md, §3.14: «каждый ставит одну фишку из рюкзака, победитель забирает обе»; значки «Коллекции» на кон
/// не ставятся) — точка подключения Рюкзака (#140, E11). Пока Рюкзака нет, работает <see cref="NoDuelStakes"/>: вызов или
/// принятие со ставкой — честный отказ <c>409 duel_stake_invalid</c>, а без ставки дуэль идёт как обычно.
/// </summary>
/// <remarks>
/// Оба метода зовутся <b>в транзакции дуэли</b> на её контексте <paramref name="db"/>, под блокировками обоих игроков
/// (пространство 9, docs/architecture/jobs.md): фишка уходит на кон вместе с вызовом или принятием, а к победителю — вместе с
/// итогом, одним коммитом. Своих транзакций и <c>SaveChanges</c> реализация не открывает — коммитит дуэль.
/// </remarks>
public interface IDuelStakes
{
    /// <summary>
    /// Поставить фишку игрока на кон дуэли: она его, в Рюкзаке, не активирована, не истекла и не стоит на кону в другой
    /// дуэли. <c>false</c> — нельзя (<c>duel_stake_invalid</c>); <c>true</c> — фишка закреплена за дуэлью и до итога не
    /// активируется и не сгорает.
    /// </summary>
    Task<bool> TryStakeAsync(AppDbContext db, Guid playerId, Guid itemId, Guid duelId, CancellationToken cancellationToken);

    /// <summary>
    /// Развязка ставок: <paramref name="winnerId"/> забирает обе фишки; <c>null</c> (ничья, отказ, вызов истёк, дуэль
    /// снята) — каждому своя. Повтор для той же дуэли ничего не меняет.
    /// </summary>
    Task SettleAsync(AppDbContext db, DuelEntity duel, Guid? winnerId, CancellationToken cancellationToken);
}

/// <summary>Рюкзака ещё нет (#140): поставить нечего — любая ставка отклоняется, развязывать нечего.</summary>
public sealed class NoDuelStakes : IDuelStakes
{
    public Task<bool> TryStakeAsync(AppDbContext db, Guid playerId, Guid itemId, Guid duelId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task SettleAsync(AppDbContext db, DuelEntity duel, Guid? winnerId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Отрезки для метрики «лучшее время на отрезке» (§3.14, §3.13) — точка подключения «Королей участков» (#146, E16). Пока
/// отрезков нет, работает <see cref="NoDuelSegments"/>: вызов на <c>segmentTime</c> — <c>400 duel_invalid</c> («такого
/// отрезка нет»), счёт таких дуэлей — <c>null</c>.
/// </summary>
public interface IDuelSegments
{
    /// <summary>Отрезок есть и открыт для лиги — на него можно вызвать.</summary>
    Task<bool> ExistsAsync(Guid segmentId, League league, CancellationToken cancellationToken);

    /// <summary>
    /// Лучшее время игрока на отрезке в лиге, мс, по проходам за период [<paramref name="from"/>, <paramref name="to"/>)
    /// (проход — по началу его забега по часам сервера, как дистанция и туман дуэли); <c>null</c> — проходов нет.
    /// <paramref name="publicOnly"/> — только проходы, которые видит посторонний: конец их забега публичен
    /// (<c>runs.visits_processed_at</c>, граница публичности, как таблица отрезка); иначе — и свои свежие (игрок смотрит
    /// свой счёт).
    /// </summary>
    Task<long?> BestTimeMsAsync(
        Guid segmentId, Guid playerId, League league, DateTimeOffset from, DateTimeOffset to, bool publicOnly, CancellationToken cancellationToken);
}

/// <summary>Отрезков ещё нет (#146): вызвать не на что, проходов нет.</summary>
public sealed class NoDuelSegments : IDuelSegments
{
    public Task<bool> ExistsAsync(Guid segmentId, League league, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<long?> BestTimeMsAsync(
        Guid segmentId, Guid playerId, League league, DateTimeOffset from, DateTimeOffset to, bool publicOnly, CancellationToken cancellationToken) =>
        Task.FromResult<long?>(null);
}

/// <summary>
/// Уведомления дуэлей во «Входящие» (§3.14: «живой счёт и уведомление о смене лидера»; §3.17: дуэль — третий приоритет,
/// бюджет 3 в день) — точка подключения «Входящих» (#139, E10). Пока их нет, работает <see cref="NoDuelNotifier"/>.
/// </summary>
/// <remarks>
/// Методы зовутся <b>в транзакции</b> смены состояния дуэли на её контексте: событие пишется вместе с ней (вызов есть —
/// есть и событие). Ничего не опережает границу публичности: вызов и итог — не про место и время, а смену лидера задача
/// <c>DuelReferee</c> считает по счёту, который видят оба (только публичные захваты и забеги), — поэтому
/// <c>visible_at</c> событий — «сейчас». Бюджет и тишину 22–08 решает «Входящие» (<c>NotificationBudget</c>), не дуэли.
/// </remarks>
public interface IDuelNotifier
{
    /// <summary>Вызов: событие — вызванному.</summary>
    Task ChallengedAsync(AppDbContext db, DuelEntity duel, CancellationToken cancellationToken);

    /// <summary>Впереди теперь <paramref name="leaderId"/> (раньше — соперник или поровну): событие — обоим.</summary>
    Task LeaderChangedAsync(AppDbContext db, DuelEntity duel, Guid leaderId, CancellationToken cancellationToken);

    /// <summary>Итог подведён (победа, ничья или дуэль снята): событие — обеим сторонам, что ещё есть.</summary>
    Task FinishedAsync(AppDbContext db, DuelEntity duel, CancellationToken cancellationToken);
}

/// <summary>«Входящих» ещё нет (#139): событий не пишем.</summary>
public sealed class NoDuelNotifier : IDuelNotifier
{
    public Task ChallengedAsync(AppDbContext db, DuelEntity duel, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task LeaderChangedAsync(AppDbContext db, DuelEntity duel, Guid leaderId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FinishedAsync(AppDbContext db, DuelEntity duel, CancellationToken cancellationToken) => Task.CompletedTask;
}
