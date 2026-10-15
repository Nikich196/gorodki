using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Gorodki.Api.Features.Admin;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gorodki.Api.Features.Social;

/// <summary>Кто этот игрок для меня.</summary>
public enum FriendStatus
{
    /// <summary>Друзья — взаимно.</summary>
    Friend,

    /// <summary>Он добавил меня по коду; я могу принять.</summary>
    Incoming,

    /// <summary>Я добавил его по коду; ждём, пока примет.</summary>
    Outgoing,
}

/// <summary>Друг или заявка.</summary>
/// <param name="Name">Ник — если игрок согласился его показывать; иначе «Игрок #1234» (правило карточки игрока).</param>
public sealed record FriendResponse(Guid PlayerId, string Name, short ColorIndex, FriendStatus Status);

/// <summary>Друзья и заявки.</summary>
/// <param name="MyCode">Свой код дружбы — для QR и ссылки.</param>
/// <param name="Friends">Друзья и заявки: сначала входящие, потом друзья, потом исходящие.</param>
public sealed record FriendsResponse(string MyCode, IReadOnlyList<FriendResponse> Friends);

/// <summary>Добавить друга по его коду (из QR или ссылки).</summary>
public sealed record AddFriendRequest
{
    [JsonRequired]
    public required string Code { get; init; }
}

/// <summary>
/// Друзья (PLAN.md, §3.8): только взаимные, по QR или ссылке со своим кодом; поиска по нику нет. Живых позиций друзей нет
/// (решение Никиты) — дружба открывает ленту «друзья и клан» и дуэли, но не координаты и не чужие захваты раньше границы
/// публичности. Задача E14a (#143, docs/guides/egor-server.md, раздел 7) — сделано Claude 15.10.
/// </summary>
/// <remarks>
/// <b>Как хранится.</b> Строка <c>friend_links</c> — «игрок добавил друга» (по коду или приняв заявку). Строки в обе стороны —
/// друзья; в одну — заявка. Поэтому встречная заявка (оба добавили друг друга по коду) — сразу дружба, без особого случая, а
/// удалить, отклонить и отозвать — одно действие: стереть обе строки пары.
/// <para>
/// <b>Код дружбы</b> — <c>users.friend_code</c>, <c>XXXX-XXXX</c> как у кланов; выдаётся при первом <c>GET /friends</c> и не
/// меняется. <b>Блокировка</b> (<c>PUT /me/blocks/{id}</c>) снимает дружбу и заявки в обе стороны; пока она стоит, заявку не
/// отправить ни ему (<c>friend_blocked</c>), ни от него (<c>friend_code_invalid</c> — блокировка себя не выдаёт). Заявка,
/// принятие и блокировка одной пары — под блокировкой пары (<see cref="LockPairAsync"/>). Аккаунт, который удаляется, в «Друзьях»
/// не виден и по коду не находится; его строки уходят каскадом вместе с ним.
/// </para>
/// </remarks>
public static class FriendEndpoints
{
    public static IEndpointRouteBuilder MapFriendEndpoints(this IEndpointRouteBuilder app)
    {
        var friends = app.MapGroup("/friends").WithTags("Друзья").RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        friends.MapGet("", ListFriends)
            .WithName("listFriends")
            .WithSummary("Друзья, входящие и исходящие заявки и свой код для QR");
        friends.MapPost("", AddFriend)
            .WithName("addFriend")
            .WithSummary("Добавить по коду: заявка; если он уже добавил меня — сразу друзья")
            .WithDescription(
                "Нет такого кода (или игрок заблокировал тебя) — 404 friend_code_invalid: блокировка так не выдаёт себя. Свой код — "
                + "400 friend_self; ты сам заблокировал этого игрока — 400 friend_blocked (сначала сними блокировку). "
                + "Ответ — игрок со статусом outgoing или friend; повтор ничего не меняет.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        friends.MapPost("/{id:guid}/accept", AcceptFriend)
            .WithName("acceptFriend")
            .WithSummary("Принять входящую заявку")
            .WithDescription("Заявки от этого игрока нет — 404 friend_request_not_found. Повтор (уже друзья) — тот же ответ.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        friends.MapDelete("/{id:guid}", RemoveFriend)
            .WithName("removeFriend")
            .WithSummary("Удалить из друзей, отклонить или отозвать заявку")
            .WithDescription("Ни дружбы, ни заявки с этим игроком нет — 404 friend_not_found.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<Results<Ok<FriendsResponse>, NotFound>> ListFriends(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } me || await MyCodeAsync(db, me, cancellationToken) is not { } myCode)
        {
            return TypedResults.NotFound();
        }

        // Все строки пары со мной — в любую сторону; другой игрок — живой и без блокировок (Others).
        var links = await db.FriendLinks.AsNoTracking()
            .Where(f => f.UserId == me || f.FriendId == me)
            .Select(f => new { OtherId = f.UserId == me ? f.FriendId : f.UserId, Mine = f.UserId == me, f.CreatedAt })
            .Join(Others(db, me), l => l.OtherId, u => u.Id, (l, u) => new
            {
                l.OtherId,
                l.Mine,
                l.CreatedAt,
                u.DisplayName,
                u.ColorIndex,
                u.PublicProfile,
            })
            .ToListAsync(cancellationToken);
        var friends = links
            .GroupBy(l => l.OtherId)
            .Select(pair =>
            {
                var other = pair.First();
                var name = NameOf(pair.Key, other.DisplayName, other.PublicProfile);
                var status = StatusOf(mine: pair.Any(l => l.Mine), theirs: pair.Any(l => !l.Mine));
                return new
                {
                    Response = new FriendResponse(pair.Key, name, other.ColorIndex, status),
                    Since = pair.Max(l => l.CreatedAt), // друзья — с момента принятия, заявка — с момента отправки
                };
            })
            .OrderBy(f => f.Response.Status switch { FriendStatus.Incoming => 0, FriendStatus.Friend => 1, _ => 2 })
            .ThenByDescending(f => f.Since)
            .ThenBy(f => f.Response.PlayerId)
            .Select(f => f.Response)
            .ToList();
        return TypedResults.Ok(new FriendsResponse(myCode, friends));
    }

    private static async Task<Results<Ok<FriendResponse>, ProblemHttpResult>> AddFriend(
        AddFriendRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } me)
        {
            return Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Нужен вход.");
        }

        // Код — как его набрали: без пробелов по краям и заглавными (коды выдаются заглавными, как у кланов).
        var code = request.Code.Trim().ToUpperInvariant();
        var target = await db.Users.AsNoTracking()
            .Where(u => u.FriendCode == code && u.DeletionRequestedAt == null)
            .Select(u => new { u.Id, u.DisplayName, u.ColorIndex, u.PublicProfile })
            .SingleOrDefaultAsync(cancellationToken);
        if (target is null)
        {
            return CodeInvalid();
        }

        if (target.Id == me)
        {
            return Problem(StatusCodes.Status400BadRequest, "friend_self", "Это твой собственный код.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockPairAsync(db, me, target.Id, cancellationToken);

        // Блокировки — уже под блокировкой пары: блокировка, поставленная в эту же секунду, заявку не пропустит.
        var blockers = await db.PlayerBlocks.AsNoTracking()
            .Where(b => (b.BlockerId == me && b.BlockedId == target.Id) || (b.BlockerId == target.Id && b.BlockedId == me))
            .Select(b => b.BlockerId)
            .ToListAsync(cancellationToken);
        if (blockers.Contains(target.Id))
        {
            return CodeInvalid(); // он заблокировал меня — «нет такого кода»: блокировка себя не выдаёт
        }

        if (blockers.Contains(me))
        {
            return Problem(
                StatusCodes.Status400BadRequest, "friend_blocked", "Ты заблокировал этого игрока — сначала сними блокировку.");
        }

        // Повтор (уже добавил) ничего не меняет; его встречная заявка уже есть — значит, теперь друзья.
        await AddLinkAsync(db, me, target.Id, time.GetUtcNow(), cancellationToken);
        var theirs = await db.FriendLinks.AnyAsync(f => f.UserId == target.Id && f.FriendId == me, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var name = NameOf(target.Id, target.DisplayName, target.PublicProfile);
        return TypedResults.Ok(new FriendResponse(target.Id, name, target.ColorIndex, StatusOf(mine: true, theirs)));
    }

    private static async Task<Results<Ok<FriendResponse>, ProblemHttpResult>> AcceptFriend(
        Guid id, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } me)
        {
            return RequestNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockPairAsync(db, me, id, cancellationToken);

        // Заявка от id именно ко мне — одним запросом (чужую заявку так не принять); автор — живой и без блокировок. Уже
        // приняли (ответ потерялся) — заявка на месте, повтор ничего не меняет.
        var requester = await db.FriendLinks.AsNoTracking()
            .Where(f => f.UserId == id && f.FriendId == me)
            .Join(Others(db, me), f => f.UserId, u => u.Id, (f, u) => new { u.DisplayName, u.ColorIndex, u.PublicProfile })
            .SingleOrDefaultAsync(cancellationToken);
        if (requester is null)
        {
            return RequestNotFound();
        }

        await AddLinkAsync(db, me, id, time.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(new FriendResponse(
            id, NameOf(id, requester.DisplayName, requester.PublicProfile), requester.ColorIndex, FriendStatus.Friend));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveFriend(
        Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        // Только пара «я — id»: условие «это моя связь» — в том же запросе (egor-server.md, раздел 4, п. 5). Обе строки
        // сразу: дружба кончается для обоих, отклонённая или отозванная заявка исчезает у обоих.
        var me = principal.UserId();
        var removed = await db.FriendLinks
            .Where(f => (f.UserId == me && f.FriendId == id) || (f.UserId == id && f.FriendId == me))
            .ExecuteDeleteAsync(cancellationToken);
        return removed > 0
            ? TypedResults.NoContent()
            : Problem(StatusCodes.Status404NotFound, "friend_not_found", "С этим игроком нет ни дружбы, ни заявки.");
    }

    // MARK: — общее

    /// <summary>
    /// Пара игроков — под блокировку PostgreSQL (пространство 7, docs/architecture/jobs.md) до конца транзакции: заявка,
    /// принятие и блокировка одной пары идут по очереди. Иначе встречные заявки в одну секунду обе ответили бы «исходящая»
    /// (каждая не видит ещё не сохранённую другую), а заявка, отправленная в момент блокировки, пережила бы её.
    /// </summary>
    internal static Task LockPairAsync(AppDbContext db, Guid first, Guid second, CancellationToken cancellationToken)
    {
        var (a, b) = (first.ToString(), second.ToString());
        var pairKey = string.CompareOrdinal(a, b) < 0 ? $"{a}:{b}" : $"{b}:{a}";
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(7, hashtext({pairKey}))", cancellationToken);
    }

    /// <summary>
    /// «<paramref name="userId"/> добавил <paramref name="friendId"/>»; такая строка уже есть — ничего не меняется.
    /// </summary>
    private static Task AddLinkAsync(
        AppDbContext db, Guid userId, Guid friendId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO app.friend_links (user_id, friend_id, created_at) VALUES ({userId}, {friendId}, {now})
            ON CONFLICT DO NOTHING
            """,
            cancellationToken);

    /// <summary>
    /// Свой код дружбы; кода ещё нет — выдаёт новый, один раз: дальше он не меняется. Два первых запроса одновременно не
    /// выдадут два разных кода — код пишется, только если его ещё нет, и затем перечитывается. <c>null</c> — игрока нет.
    /// </summary>
    private static async Task<string?> MyCodeAsync(AppDbContext db, Guid me, CancellationToken cancellationToken)
    {
        while (true)
        {
            var user = await db.Users.AsNoTracking()
                .Where(u => u.Id == me)
                .Select(u => new { u.FriendCode })
                .SingleOrDefaultAsync(cancellationToken);
            if (user is null)
            {
                return null;
            }

            if (user.FriendCode is { } code)
            {
                return code;
            }

            try
            {
                var fresh = NewCode();
                await db.Users
                    .Where(u => u.Id == me && u.FriendCode == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(u => u.FriendCode, fresh), cancellationToken);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Такой код уже у другого игрока (шанс — около одного на триллион): следующий проход возьмёт другой.
            }
        }
    }

    /// <summary>
    /// Новый код дружбы <c>XXXX-XXXX</c> — как код клана: алфавит Крокфорда без похожих символов (I, L, O, U). Случайность —
    /// только <see cref="RandomNumberGenerator"/>: у <c>Random</c> следующий код угадывается по предыдущим, а по коду
    /// добавляют в друзья.
    /// </summary>
    private static string NewCode()
    {
        var raw = RandomNumberGenerator.GetString(InviteCodes.Alphabet, 8);
        return $"{raw[..4]}-{raw[4..]}";
    }

    /// <summary>
    /// Другие игроки, которых я могу видеть в «Друзьях»: аккаунт не удаляется (человек попросил его забыть) и блокировки нет
    /// ни в одну сторону. Блокировка и так стирает дружбу и заявки, а этот отбор держит правило и тогда, когда строка всё же
    /// есть.
    /// </summary>
    private static IQueryable<UserEntity> Others(AppDbContext db, Guid me) =>
        db.Users.AsNoTracking().Where(u => u.DeletionRequestedAt == null
            && !db.PlayerBlocks.Any(b =>
                (b.BlockerId == me && b.BlockedId == u.Id) || (b.BlockerId == u.Id && b.BlockedId == me)));

    /// <summary>Строка от меня и строка от него — друзья; только моя — исходящая заявка; только его — входящая.</summary>
    private static FriendStatus StatusOf(bool mine, bool theirs) => (mine, theirs) switch
    {
        (true, true) => FriendStatus.Friend,
        (true, false) => FriendStatus.Outgoing,
        _ => FriendStatus.Incoming,
    };

    /// <summary>
    /// Ник — по правилу карточки игрока (#115): только с согласия, иначе «Игрок #1234»; дружба согласия не заменяет.
    /// </summary>
    private static string NameOf(Guid id, string displayName, bool publicProfile) =>
        publicProfile ? displayName : LeaderboardEndpoints.Pseudonym(id);

    private static ProblemHttpResult CodeInvalid() =>
        Problem(StatusCodes.Status404NotFound, "friend_code_invalid", "Такого кода нет.");

    private static ProblemHttpResult RequestNotFound() =>
        Problem(StatusCodes.Status404NotFound, "friend_request_not_found", "Заявки от этого игрока нет.");

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
