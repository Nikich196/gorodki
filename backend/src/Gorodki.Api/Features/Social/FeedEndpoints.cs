using System.Globalization;
using System.Security.Claims;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Players;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Social;

/// <summary>О чём пост.</summary>
public enum FeedPostKind
{
    /// <summary>Захват: сколько земли взято.</summary>
    Capture,

    /// <summary>Забег: сколько пройдено.</summary>
    Run,
}

/// <summary>Пост ленты — только числа, без координат и времени.</summary>
/// <param name="Id">Номер поста: случайный (<c>Guid.NewGuid()</c>), не по времени — по нему не узнать, когда был захват.</param>
/// <param name="AuthorName">Ник — если автор согласился его показывать или это ты сам; иначе «Игрок #1234».</param>
/// <param name="Day">Игровые сутки (по Минску), <c>yyyy-MM-dd</c> — дата без времени.</param>
/// <param name="DistanceMeters">У забега — засчитанный путь, м, округлён до 1; у захвата — <c>null</c>.</param>
/// <param name="CapturedSquareMeters">У захвата — взятая площадь, м², округлена до 1; у забега — <c>null</c>.</param>
/// <param name="RespectedByMe">Я уже поставил респект.</param>
/// <param name="Mine">Мой пост.</param>
public sealed record FeedPost(
    Guid Id,
    Guid AuthorId,
    string AuthorName,
    short AuthorColorIndex,
    FeedPostKind Kind,
    League League,
    string Day,
    double? DistanceMeters,
    double? CapturedSquareMeters,
    int Respects,
    bool RespectedByMe,
    bool Mine);

/// <summary>Страница ленты, новые сверху.</summary>
/// <param name="NextCursor">Курсор следующей (более старой) страницы; <c>null</c> — дальше ничего нет.</param>
public sealed record FeedResponse(IReadOnlyList<FeedPost> Posts, string? NextCursor);

/// <summary>Жалоба на пост.</summary>
/// <param name="Reason">Причина своими словами, до 200 символов; можно без неё.</param>
public sealed record ReportPostRequest(string? Reason);

/// <summary>
/// Лента (PLAN.md, §3.8: по умолчанию «друзья и клан»; респекты, жалобы, блокировки). Комментариев нет (карточка E14b).
/// Посты — из захватов и забегов, только числа. <b>Граница публичности:</b> пост о захвате виден не раньше самого захвата на
/// карте — <c>visible_at</c> через <c>TerritoryReader.VisibleAtAsync(автор, applied_at)</c>; пост о забеге — когда конец забега
/// публичен (<c>runs.visits_processed_at</c>). До этого поста нет ни в ленте, ни для респекта и жалобы (404). Задача #144
/// (E14b) — сделано Claude 07.10.
/// </summary>
/// <remarks>
/// <b>«Друзья и клан».</b> Пока друзей нет (#143, задача Егора), лента <c>friends</c> — свои посты и посты соклановцев
/// (текущий состав клана); друзья добавятся в <see cref="Circle"/> одной строкой. Курсор страницы — номер последнего поста
/// (случайный), а не время: по курсору нельзя узнать, когда был захват.
/// </remarks>
public static class FeedEndpoints
{
    /// <summary>Постов на странице.</summary>
    public const int PageSize = 30;

    /// <summary>Причина жалобы — не длиннее.</summary>
    public const int MaxReasonLength = 200;

    public static IEndpointRouteBuilder MapFeedEndpoints(this IEndpointRouteBuilder app)
    {
        var feed = app.MapGroup("/feed").WithTags("Лента").RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        feed.MapGet("", GetFeed)
            .WithName("getFeed")
            .WithSummary("Лента: scope=friends (друзья и клан, по умолчанию) или all; по 30, cursor — из nextCursor")
            .WithDescription(
                "Только посты, ставшие публичными (захват — с той же границы, что на карте). Посты заблокированных и заблокировавших "
                + "не видны. Неверный scope или курсор — 400 feed_invalid.")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        feed.MapPost("/{id:guid}/respect", RespectPost)
            .WithName("respectPost")
            .WithSummary("Поставить респект (повтор ничего не меняет)")
            .WithDescription("Поста нет или он тебе не виден (ещё до границы публичности, блокировка) — 404 post_not_found.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        feed.MapPost("/{id:guid}/report", ReportPost)
            .WithName("reportPost")
            .WithSummary("Пожаловаться на пост — его посмотрит админ")
            .WithDescription("Поста нет или он тебе не виден — 404 post_not_found; причина длиннее 200 — 400 report_invalid.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var blocks = app.MapGroup("/me/blocks").WithTags("Лента").RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        blocks.MapGet("", ListBlocks)
            .WithName("listBlocks")
            .WithSummary("Кого я заблокировал");
        blocks.MapPut("/{id:guid}", BlockPlayer)
            .WithName("blockPlayer")
            .WithSummary("Заблокировать игрока: его посты не видны, дружба и заявки с ним снимаются")
            .WithDescription("Себя — 400 block_self. Повтор ничего не меняет.")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        blocks.MapDelete("/{id:guid}", UnblockPlayer)
            .WithName("unblockPlayer")
            .WithSummary("Снять блокировку (повтор ничего не меняет)");
        return app;
    }

    private static async Task<Results<Ok<FeedResponse>, ProblemHttpResult>> GetFeed(
        string? scope, string? cursor, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } me)
        {
            return Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Нужен вход.");
        }

        var friendsOnly = scope is null or "friends";
        if (!friendsOnly && scope != "all")
        {
            return FeedInvalid();
        }

        var now = time.GetUtcNow();
        var source = db.FeedPosts.AsNoTracking();
        if (cursor is not null)
        {
            // Курсор — номер последнего поста прошлой страницы; ищется тем же отбором, что сама лента.
            if (!Guid.TryParse(cursor, out var after)
                || await Visible(db, me, now).Where(p => p.Id == after).Select(p => new { p.VisibleAt, p.Id }).SingleOrDefaultAsync(cancellationToken)
                    is not { } last)
            {
                return FeedInvalid();
            }

            source = db.FeedPosts.FromSql($"SELECT * FROM app.feed_posts WHERE (visible_at, id) < ({last.VisibleAt}, {last.Id})").AsNoTracking();
        }

        var posts = Visible(db, me, now, source);
        if (friendsOnly)
        {
            posts = posts.Where(Circle(db, me));
        }

        var page = await posts
            .OrderByDescending(p => p.VisibleAt)
            .ThenByDescending(p => p.Id)
            .Take(PageSize + 1)
            .Join(db.Users, p => p.AuthorId, u => u.Id, (p, u) => new
            {
                Post = p,
                u.DisplayName,
                u.ColorIndex,
                u.PublicProfile,
                Respects = db.FeedRespects.Count(r => r.PostId == p.Id),
                RespectedByMe = db.FeedRespects.Any(r => r.PostId == p.Id && r.UserId == me),
            })
            .ToListAsync(cancellationToken);
        var ordered = page.OrderByDescending(r => r.Post.VisibleAt).ThenByDescending(r => r.Post.Id).Take(PageSize).ToList();
        var result = ordered
            .Select(r => new FeedPost(
                r.Post.Id,
                r.Post.AuthorId,
                r.Post.AuthorId == me || r.PublicProfile ? r.DisplayName : LeaderboardEndpoints.Pseudonym(r.Post.AuthorId),
                r.ColorIndex,
                r.Post.Kind,
                r.Post.League,
                r.Post.GameDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Post.DistanceMeters,
                r.Post.CapturedSquareMeters,
                r.Respects,
                r.RespectedByMe,
                r.Post.AuthorId == me))
            .ToList();
        return TypedResults.Ok(new FeedResponse(result, page.Count > PageSize ? ordered[^1].Post.Id.ToString() : null));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RespectPost(
        Guid id, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } me || !await Visible(db, me, time.GetUtcNow()).AnyAsync(p => p.Id == id, cancellationToken))
        {
            return PostNotFound();
        }

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO app.feed_respects (post_id, user_id) VALUES ({id}, {me}) ON CONFLICT DO NOTHING", cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ReportPost(
        Guid id, ReportPostRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (request.Reason is { Length: > MaxReasonLength })
        {
            return Problem(StatusCodes.Status400BadRequest, "report_invalid", $"Причина — не длиннее {MaxReasonLength} символов.");
        }

        var now = time.GetUtcNow();
        if (principal.UserId() is not { } me || !await Visible(db, me, now).AnyAsync(p => p.Id == id, cancellationToken))
        {
            return PostNotFound();
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO app.feed_reports (post_id, reporter_id, reason, created_at) VALUES ({id}, {me}, {reason}, {now})
            ON CONFLICT (post_id, reporter_id) DO NOTHING
            """,
            cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<IReadOnlyList<PlayerResponse>>> ListBlocks(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        var me = principal.UserId();
        var blocked = await db.PlayerBlocks.AsNoTracking()
            .Where(b => b.BlockerId == me)
            .Join(db.Users.Where(u => u.DeletionRequestedAt == null), b => b.BlockedId, u => u.Id, (b, u) => new
            {
                u.Id,
                u.DisplayName,
                u.ColorIndex,
                u.PublicProfile,
                b.CreatedAt,
            })
            .OrderBy(b => b.CreatedAt)
            .ThenBy(b => b.Id)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyList<PlayerResponse>>(
        [
            .. blocked.Select(b => new PlayerResponse(
                b.Id, b.PublicProfile ? b.DisplayName : LeaderboardEndpoints.Pseudonym(b.Id), b.ColorIndex, IsMe: false)),
        ]);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> BlockPlayer(
        Guid id, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } me)
        {
            return Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Нужен вход.");
        }

        if (id == me)
        {
            return Problem(StatusCodes.Status400BadRequest, "block_self", "Себя заблокировать нельзя.");
        }

        // Нет такого игрока — тоже 204, ничего не записано: блокировка не подтверждает, что номер существует.
        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO app.player_blocks (blocker_id, blocked_id, created_at)
            SELECT {me}, id, {now} FROM app.users WHERE id = {id}
            ON CONFLICT DO NOTHING
            """,
            cancellationToken);

        // ЗАДАЧА #143 (Егор, E14a): здесь же снять дружбу и заявки в обе стороны с этим игроком — когда появится таблица друзей.
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> UnblockPlayer(
        Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        var me = principal.UserId();
        await db.PlayerBlocks.Where(b => b.BlockerId == me && b.BlockedId == id).ExecuteDeleteAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // MARK: — общее

    /// <summary>
    /// Посты, которые видит <paramref name="me"/>: уже публичные (<c>visible_at ≤ now</c>, граница — как у карты), без
    /// блокировок в обе стороны, без авторов, чей аккаунт удаляется. Один отбор для ленты, курсора, респекта и жалобы: по
    /// ответу 404 нельзя проверить, есть ли ещё скрытый пост.
    /// </summary>
    private static IQueryable<FeedPostEntity> Visible(
        AppDbContext db, Guid me, DateTimeOffset now, IQueryable<FeedPostEntity>? source = null) =>
        (source ?? db.FeedPosts.AsNoTracking())
            .Where(p => p.VisibleAt <= now
                && !db.PlayerBlocks.Any(b => (b.BlockerId == me && b.BlockedId == p.AuthorId) || (b.BlockerId == p.AuthorId && b.BlockedId == me))
                && db.Users.Any(u => u.Id == p.AuthorId && u.DeletionRequestedAt == null));

    /// <summary>
    /// «Друзья и клан» (§3.8): свои посты и посты соклановцев по текущему составу клана. Друзья (#143) добавятся сюда условием
    /// «автор — взаимный друг».
    /// </summary>
    private static System.Linq.Expressions.Expression<Func<FeedPostEntity, bool>> Circle(AppDbContext db, Guid me) =>
        p => p.AuthorId == me
            || db.ClanMembers.Any(m => m.UserId == p.AuthorId && db.ClanMembers.Any(mine => mine.UserId == me && mine.ClanId == m.ClanId));

    private static ProblemHttpResult FeedInvalid() =>
        Problem(StatusCodes.Status400BadRequest, "feed_invalid", "scope — friends или all; курсор — из nextCursor прошлой страницы.");

    private static ProblemHttpResult PostNotFound() =>
        Problem(StatusCodes.Status404NotFound, "post_not_found", "Такого поста нет.");

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
