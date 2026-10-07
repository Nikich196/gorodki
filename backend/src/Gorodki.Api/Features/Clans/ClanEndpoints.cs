using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Gorodki.Api.Features.Admin;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Clans;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gorodki.Api.Features.Clans;

/// <summary>Участник клана глазами спрашивающего.</summary>
/// <param name="Name">Ник — если игрок согласился его показывать или это ты сам; иначе «Игрок #1234», как в рейтинге и карточке игрока.</param>
/// <param name="ColorIndex">Цвет земли игрока — номер в палитре из 12.</param>
/// <param name="Me">Это сам спрашивающий.</param>
public sealed record ClanMemberResponse(Guid PlayerId, string Name, short ColorIndex, ClanRole Role, bool Me);

/// <summary>Клан.</summary>
/// <param name="Hue">Оттенок клана — номер в палитре кланов из 12 (0–11).</param>
/// <param name="Full">В клане 3 человека и больше; «неполный» клан остаётся, но не участвует в рейдах и контроле кварталов (§3.6).</param>
/// <param name="Members">Участники: лидер, офицеры, остальные — по дате вступления.</param>
/// <param name="MyRole">Роль спрашивающего в этом клане; <c>null</c> — он не участник.</param>
/// <param name="InviteCode">Код-приглашение — только лидеру и офицерам этого клана, остальным <c>null</c>.</param>
public sealed record ClanResponse(
    Guid Id, string Name, int Hue, bool Full, IReadOnlyList<ClanMemberResponse> Members, ClanRole? MyRole, string? InviteCode);

/// <summary>Свой клан или когда можно вступить.</summary>
/// <param name="Clan">Свой клан; <c>null</c> — игрок не в клане.</param>
/// <param name="CanJoinAtMs">
/// Не в клане и вышел или исключён меньше 72 ч назад — с какого момента можно вступить или создать клан (мс Unix);
/// иначе <c>null</c>.
/// </param>
public sealed record MyClanResponse(ClanResponse? Clan, long? CanJoinAtMs);

/// <summary>Создать клан.</summary>
public sealed record CreateClanRequest
{
    /// <summary>Название: 3–24 символа (буквы, цифры, пробел, дефис), уникально без учёта регистра.</summary>
    [JsonRequired]
    public required string Name { get; init; }

    /// <summary>Оттенок из палитры кланов (0–11); <c>null</c> — сервер выберет свободный (<c>GET /clans/hues</c>).</summary>
    public int? Hue { get; init; }
}

/// <summary>Вступить в клан по коду-приглашению от лидера или офицера.</summary>
public sealed record JoinClanRequest
{
    [JsonRequired]
    public required string Code { get; init; }
}

/// <summary>Назначить роль участнику.</summary>
public sealed record ClanRoleRequest
{
    /// <summary><c>officer</c> или <c>member</c>. Лидерство так не передаётся: оно переходит само, когда лидер выходит.</summary>
    [JsonRequired]
    public required ClanRole Role { get; init; }
}

/// <summary>Переименовать клан.</summary>
public sealed record RenameClanRequest
{
    /// <summary>Новое название — те же правила, что при создании.</summary>
    [JsonRequired]
    public required string Name { get; init; }
}

/// <summary>Свободные оттенки палитры кланов.</summary>
/// <param name="Free">Оттенки 0–11, которых нет ни у одного клана. Пусто — кланов больше 12: можно любой, оттенок повторится.</param>
public sealed record ClanHuesResponse(IReadOnlyList<int> Free);

/// <summary>
/// Кланы (PLAN.md, §3.3, §3.6; решения по #64 от 25.09): 3–12 человек, лидер и до 2 офицеров, вступление только по коду от
/// лидера или офицера — без заявок и модерации. Земля личная: выход и исключение её не трогают. Кланы в движке земли и на
/// карте — задача Claude (C8), здесь их нет. Задачи Егора: E5a — создать, вступить, свой клан, карточка; E5b — выход,
/// исключение, роли; E5c — переименование, оттенки, новый код (docs/guides/egor-server.md, раздел 7). Сделано Claude 07.10 по
/// перераспределению задач; правила — чистые функции <see cref="ClanRules"/>.
/// </summary>
/// <remarks>
/// Действия над своим кланом — по адресам <c>/clans/mine/…</c>: номер клана в них не нужен, чужой клан так не тронуть.
/// Номер участника в пути (<c>/clans/mine/members/{userId}</c>) ищется только в своём клане — чужой игрок: 404.
/// <para>
/// Гонки — под блокировками строк: сначала строка клана (<c>FOR UPDATE</c>), потом строка игрока — у всех адресов в одном
/// порядке, взаимной блокировки нет. Потолок 12, предел офицеров и «один лидер» проверяются под блокировкой клана; «один клан на
/// игрока» и 72 ч — под блокировкой игрока; базу страхуют ключ участника (игрок) и уникальный индекс лидера.
/// </para>
/// </remarks>
public static class ClanEndpoints
{
    /// <summary>Потолок участников (§3.6).</summary>
    public const int MaxMembers = ClanRules.MaxMembers;

    /// <summary>С этого числа участников клан «полный» (§3.6).</summary>
    public const int FullFrom = ClanRules.FullFrom;

    /// <summary>Офицеров — не больше (§3.6).</summary>
    public const int MaxOfficers = ClanRules.MaxOfficers;

    /// <summary>Оттенков в палитре кланов (§3.6).</summary>
    public const int Hues = ClanRules.Hues;

    /// <summary>После выхода или исключения — столько без вступления в другой клан (§3.3).</summary>
    public static readonly TimeSpan JoinCooldown = ClanRules.JoinCooldown;

    public static IEndpointRouteBuilder MapClanEndpoints(this IEndpointRouteBuilder app)
    {
        var clans = app.MapGroup("/clans").WithTags("Кланы").RequireRateLimiting(CaptureEndpoints.ReadRateLimitPolicy);
        clans.MapPost("", CreateClan)
            .WithName("createClan")
            .WithSummary("Создать клан: название и, по желанию, оттенок; создатель — лидер")
            .WithDescription(
                "Название 3–24 символа (буквы, цифры, пробел, дефис), уникально без учёта регистра, без мата — иначе 400 "
                + "clan_name_invalid, занято — 409 clan_name_taken. Уже в клане — 409 clan_already_member; вышел или исключён меньше "
                + "72 ч назад — 409 clan_join_cooldown. Оттенок не 0–11 — 400 clan_hue_invalid; занят, хотя свободные есть, — 409 "
                + "clan_hue_taken.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
        clans.MapGet("/mine", GetMyClan)
            .WithName("getMyClan")
            .WithSummary("Свой клан; не в клане — когда можно вступить");
        clans.MapGet("/hues", GetClanHues)
            .WithName("getClanHues")
            .WithSummary("Свободные оттенки палитры кланов (для экрана создания)");
        clans.MapGet("/{id:guid}", GetClan)
            .WithName("getClan")
            .WithSummary("Карточка клана: название, оттенок, участники (ники — с их согласия)")
            .WithDescription("Код-приглашение — только лидеру и офицерам этого клана. Нет такого клана — 404 clan_not_found.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        clans.MapPost("/join", JoinClan)
            .WithName("joinClan")
            .WithSummary("Вступить в клан по коду-приглашению")
            .WithDescription(
                "Нет такого кода — 404 clan_code_invalid; в клане уже 12 — 409 clan_full; уже в клане — 409 clan_already_member; "
                + "вышел или исключён меньше 72 ч назад — 409 clan_join_cooldown.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        clans.MapPost("/mine/leave", LeaveClan)
            .WithName("leaveClan")
            .WithSummary("Выйти из клана; 72 ч потом нельзя вступить в другой")
            .WithDescription(
                "Земля остаётся у игрока (она личная). Вышел лидер — лидерство переходит офицеру, иначе самому давнему участнику. "
                + "Не в клане — 404 clan_not_member.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        clans.MapDelete("/mine/members/{userId:guid}", RemoveClanMember)
            .WithName("removeClanMember")
            .WithSummary("Исключить участника (лидер — любого, офицер — рядового)")
            .WithDescription(
                "Исключённому 72 ч нельзя вступить в другой клан, земля остаётся у него. Такого участника в своём клане нет — 404 "
                + "clan_member_not_found; не хватает прав — 403 clan_forbidden.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        clans.MapPut("/mine/members/{userId:guid}/role", SetClanMemberRole)
            .WithName("setClanMemberRole")
            .WithSummary("Назначить офицером или снять (только лидер; офицеров не больше 2)")
            .WithDescription(
                "Роль — officer или member (иначе 400 clan_role_invalid). Офицеров уже 2 — 409 clan_officer_limit. Не лидер — 403 "
                + "clan_forbidden; такого участника в своём клане нет — 404 clan_member_not_found.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        clans.MapPut("/mine/name", RenameClan)
            .WithName("renameClan")
            .WithSummary("Переименовать клан (только лидер, раз в сезон)")
            .WithDescription(
                "Правила названия — как при создании (400 clan_name_invalid, 409 clan_name_taken). Уже переименовывали в этом сезоне "
                + "— 409 clan_rename_limit; не лидер — 403 clan_forbidden.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        clans.MapPost("/mine/code", NewClanCode)
            .WithName("newClanCode")
            .WithSummary("Новый код-приглашение (лидер или офицер); старый перестаёт действовать")
            .ProducesProblem(StatusCodes.Status403Forbidden);
        return app;
    }

    private static async Task<Results<Created<ClanResponse>, ProblemHttpResult>> CreateClan(
        CreateClanRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return Problem(StatusCodes.Status403Forbidden, "clan_forbidden", "Нужен вход.");
        }

        if (ClanRules.Check(request.Name) != ClanNameProblem.None)
        {
            return NameInvalid();
        }

        if (request.Hue is { } wanted && wanted is < 0 or >= ClanRules.Hues)
        {
            return Problem(StatusCodes.Status400BadRequest, "clan_hue_invalid", $"Оттенок — номер от 0 до {ClanRules.Hues - 1}.");
        }

        var now = time.GetUtcNow();
        var normalized = ClanRules.Normalize(request.Name);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockUserAsync(db, userId, cancellationToken) is not { } user)
        {
            return Problem(StatusCodes.Status403Forbidden, "clan_forbidden", "Игрока нет.");
        }

        if (await JoinBlockedAsync(db, user, now, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        if (await db.Clans.AnyAsync(c => c.NormalizedName == normalized, cancellationToken))
        {
            return NameTaken();
        }

        var used = await db.Clans.Select(c => (int)c.Hue).ToListAsync(cancellationToken);
        var free = ClanRules.FreeHues(used);
        if (request.Hue is { } hue && free.Count > 0 && !free.Contains(hue))
        {
            return Problem(StatusCodes.Status409Conflict, "clan_hue_taken", "Этот оттенок занят — выбери свободный (GET /clans/hues).");
        }

        var clan = new ClanEntity
        {
            Id = Guid.CreateVersion7(),
            Name = ClanRules.Clean(request.Name),
            NormalizedName = normalized,
            Hue = (short)(request.Hue ?? ClanRules.PickHue(used)),
            InviteCode = await NewCodeAsync(db, cancellationToken),
            CreatedAt = now,
        };
        db.Clans.Add(clan);
        db.ClanMembers.Add(new ClanMemberEntity { UserId = userId, ClanId = clan.Id, Role = ClanRole.Leader, JoinedAt = now });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return NameTaken(); // то же название успел занять другой запрос
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"/clans/{clan.Id}", (await ReadAsync(db, clan.Id, userId, cancellationToken))!);
    }

    private static async Task<Results<Ok<MyClanResponse>, NotFound>> GetMyClan(
        ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.ClanJoinAfter, ClanId = db.ClanMembers.Where(m => m.UserId == u.Id).Select(m => (Guid?)m.ClanId).FirstOrDefault() })
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null || userId is not { } me)
        {
            return TypedResults.NotFound();
        }

        if (user.ClanId is { } clanId)
        {
            return TypedResults.Ok(new MyClanResponse(await ReadAsync(db, clanId, me, cancellationToken), null));
        }

        var canJoinAt = user.ClanJoinAfter > time.GetUtcNow() ? user.ClanJoinAfter.Value.ToUnixTimeMilliseconds() : (long?)null;
        return TypedResults.Ok(new MyClanResponse(null, canJoinAt));
    }

    private static async Task<Ok<ClanHuesResponse>> GetClanHues(AppDbContext db, CancellationToken cancellationToken)
    {
        // Строгой уникальности «в Арене» пока нет: она появится после границ Арены (карточка E5, §3.6).
        var used = await db.Clans.AsNoTracking().Select(c => (int)c.Hue).ToListAsync(cancellationToken);
        return TypedResults.Ok(new ClanHuesResponse(ClanRules.FreeHues(used)));
    }

    private static async Task<Results<Ok<ClanResponse>, ProblemHttpResult>> GetClan(
        Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        return await ReadAsync(db, id, principal.UserId(), cancellationToken) is { } clan
            ? TypedResults.Ok(clan)
            : Problem(StatusCodes.Status404NotFound, "clan_not_found", "Такого клана нет.");
    }

    private static async Task<Results<Ok<ClanResponse>, ProblemHttpResult>> JoinClan(
        JoinClanRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId)
        {
            return Problem(StatusCodes.Status403Forbidden, "clan_forbidden", "Нужен вход.");
        }

        var code = request.Code.Trim().ToUpperInvariant();
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Порядок блокировок — клан, потом игрок (как у остальных адресов). Код проверяется под блокировкой клана: новый код,
        // выданный в эту секунду, старый уже не пропустит.
        var clan = await db.Clans.FromSql($"SELECT * FROM app.clans WHERE invite_code = {code} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (clan is null)
        {
            return Problem(StatusCodes.Status404NotFound, "clan_code_invalid", "Такого кода нет.");
        }

        if (await LockUserAsync(db, userId, cancellationToken) is not { } user)
        {
            return Problem(StatusCodes.Status403Forbidden, "clan_forbidden", "Игрока нет.");
        }

        if (await JoinBlockedAsync(db, user, now, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        if (await db.ClanMembers.CountAsync(m => m.ClanId == clan.Id, cancellationToken) >= ClanRules.MaxMembers)
        {
            return Problem(StatusCodes.Status409Conflict, "clan_full", $"В клане уже {ClanRules.MaxMembers} человек.");
        }

        db.ClanMembers.Add(new ClanMemberEntity { UserId = userId, ClanId = clan.Id, Role = ClanRole.Member, JoinedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await ReadAsync(db, clan.Id, userId, cancellationToken))!);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> LeaveClan(
        ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (principal.UserId() is not { } userId
            || await db.ClanMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => (Guid?)m.ClanId).SingleOrDefaultAsync(cancellationToken)
                is not { } clanId)
        {
            return Problem(StatusCodes.Status404NotFound, "clan_not_member", "Ты не в клане.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockClanAsync(db, clanId, cancellationToken);
        if (!await RemoveMemberAsync(db, clanId, userId, time.GetUtcNow() + ClanRules.JoinCooldown, cancellationToken))
        {
            return Problem(StatusCodes.Status404NotFound, "clan_not_member", "Ты не в клане."); // успел выйти другим запросом
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveClanMember(
        Guid userId, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var actorId = principal.UserId();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockedPairAsync(db, actorId, userId, cancellationToken) is not { } pair)
        {
            return MemberNotFound();
        }

        var (actor, target) = pair;

        if (actor.UserId == target.UserId || !ClanRules.CanRemove(actor.Role, target.Role))
        {
            return Forbidden();
        }

        await RemoveMemberAsync(db, actor.ClanId, target.UserId, time.GetUtcNow() + ClanRules.JoinCooldown, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<ClanResponse>, ProblemHttpResult>> SetClanMemberRole(
        Guid userId, ClanRoleRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        if (request.Role is not (ClanRole.Officer or ClanRole.Member))
        {
            return Problem(
                StatusCodes.Status400BadRequest, "clan_role_invalid", "Роль — officer или member: лидерство переходит само, когда лидер выходит.");
        }

        var actorId = principal.UserId();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockedPairAsync(db, actorId, userId, cancellationToken) is not { } pair)
        {
            return MemberNotFound();
        }

        var (actor, target) = pair;

        if (actor.Role != ClanRole.Leader || actor.UserId == target.UserId)
        {
            return Forbidden();
        }

        if (target.Role != request.Role)
        {
            var officers = await db.ClanMembers.CountAsync(m => m.ClanId == actor.ClanId && m.Role == ClanRole.Officer, cancellationToken);
            if (request.Role == ClanRole.Officer && officers >= ClanRules.MaxOfficers)
            {
                return Problem(StatusCodes.Status409Conflict, "clan_officer_limit", $"Офицеров уже {ClanRules.MaxOfficers}.");
            }

            await db.ClanMembers
                .Where(m => m.UserId == target.UserId && m.ClanId == actor.ClanId)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.Role, request.Role), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await ReadAsync(db, actor.ClanId, actor.UserId, cancellationToken))!);
    }

    private static async Task<Results<Ok<ClanResponse>, ProblemHttpResult>> RenameClan(
        RenameClanRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        SeasonStore seasons,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (ClanRules.Check(request.Name) != ClanNameProblem.None)
        {
            return NameInvalid();
        }

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockedOwnAsync(db, principal.UserId(), cancellationToken) is not { Role: ClanRole.Leader } leader)
        {
            return Forbidden();
        }

        // Раз в сезон (§3.6): сезон по календарю; до первого сезона (и вне календаря) — один общий период.
        var calendar = await seasons.CalendarAsync(cancellationToken);
        var renamedAt = await db.Clans.Where(c => c.Id == leader.ClanId).Select(c => c.RenamedAt).SingleAsync(cancellationToken);
        if (renamedAt is { } last && calendar.At(last)?.Number == calendar.At(now)?.Number)
        {
            return Problem(StatusCodes.Status409Conflict, "clan_rename_limit", "Переименовать клан можно раз в сезон.");
        }

        var normalized = ClanRules.Normalize(request.Name);
        if (await db.Clans.AnyAsync(c => c.NormalizedName == normalized && c.Id != leader.ClanId, cancellationToken))
        {
            return NameTaken();
        }

        try
        {
            await db.Clans
                .Where(c => c.Id == leader.ClanId)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(c => c.Name, ClanRules.Clean(request.Name))
                        .SetProperty(c => c.NormalizedName, normalized)
                        .SetProperty(c => c.RenamedAt, now),
                    cancellationToken);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return NameTaken();
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await ReadAsync(db, leader.ClanId, leader.UserId, cancellationToken))!);
    }

    private static async Task<Results<Ok<ClanResponse>, ProblemHttpResult>> NewClanCode(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockedOwnAsync(db, principal.UserId(), cancellationToken) is not { Role: ClanRole.Leader or ClanRole.Officer } member)
        {
            return Forbidden();
        }

        var code = await NewCodeAsync(db, cancellationToken);
        await db.Clans.Where(c => c.Id == member.ClanId).ExecuteUpdateAsync(set => set.SetProperty(c => c.InviteCode, code), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await ReadAsync(db, member.ClanId, member.UserId, cancellationToken))!);
    }

    // MARK: — общее

    /// <summary>
    /// Клан глазами <paramref name="viewerId"/>: участники (кроме тех, чей аккаунт удаляется), ник — по правилу карточки
    /// игрока (#115), код — только лидеру и офицерам этого клана. <c>null</c> — такого клана нет.
    /// </summary>
    internal static async Task<ClanResponse?> ReadAsync(AppDbContext db, Guid clanId, Guid? viewerId, CancellationToken cancellationToken)
    {
        var clan = await db.Clans.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clanId, cancellationToken);
        if (clan is null)
        {
            return null;
        }

        var members = await db.ClanMembers.AsNoTracking()
            .Where(m => m.ClanId == clanId)
            .Join(db.Users.Where(u => u.DeletionRequestedAt == null), m => m.UserId, u => u.Id, (m, u) => new
            {
                m.UserId,
                m.Role,
                m.JoinedAt,
                u.DisplayName,
                u.ColorIndex,
                u.PublicProfile,
            })
            .ToListAsync(cancellationToken);
        var ordered = members
            .OrderByDescending(m => m.Role)
            .ThenBy(m => m.JoinedAt)
            .ThenBy(m => m.UserId)
            .Select(m => new ClanMemberResponse(
                m.UserId,
                m.UserId == viewerId || m.PublicProfile ? m.DisplayName : LeaderboardEndpoints.Pseudonym(m.UserId),
                m.ColorIndex,
                m.Role,
                m.UserId == viewerId))
            .ToList();
        var myRole = members.Where(m => m.UserId == viewerId).Select(m => (ClanRole?)m.Role).SingleOrDefault();
        return new ClanResponse(
            clan.Id,
            clan.Name,
            clan.Hue,
            members.Count >= ClanRules.FullFrom,
            ordered,
            myRole,
            myRole is ClanRole.Leader or ClanRole.Officer ? clan.InviteCode : null);
    }

    /// <summary>
    /// Убирает участника из клана (выход, исключение, удаление аккаунта) — под блокировкой строки клана: ушёл лидер —
    /// лидерство переходит (<see cref="ClanRules.Successor"/>), ушёл последний — клана больше нет. Земля не трогается: она
    /// личная (§3.3). <paramref name="joinAfter"/> — с какого момента ушедший может вступить снова; <c>null</c> — не ставить
    /// (аккаунт стирается). False — игрока в этом клане уже нет.
    /// </summary>
    internal static async Task<bool> RemoveMemberAsync(
        AppDbContext db, Guid clanId, Guid userId, DateTimeOffset? joinAfter, CancellationToken cancellationToken)
    {
        var members = await db.ClanMembers.AsNoTracking()
            .Where(m => m.ClanId == clanId)
            .Select(m => new { m.UserId, m.Role, m.JoinedAt, Deleting = db.Users.Any(u => u.Id == m.UserId && u.DeletionRequestedAt != null) })
            .ToListAsync(cancellationToken);
        var leaving = members.SingleOrDefault(m => m.UserId == userId);
        if (leaving is null)
        {
            return false;
        }

        await db.ClanMembers.Where(m => m.UserId == userId && m.ClanId == clanId).ExecuteDeleteAsync(cancellationToken);
        if (joinAfter is { } after)
        {
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(set => set.SetProperty(u => u.ClanJoinAfter, after), cancellationToken);
        }

        var remaining = members.Where(m => m.UserId != userId).ToList();
        if (remaining.Count == 0)
        {
            await db.Clans.Where(c => c.Id == clanId).ExecuteDeleteAsync(cancellationToken);
            return true;
        }

        if (leaving.Role == ClanRole.Leader)
        {
            // Лидер — из тех, кто остаётся; аккаунт, который удаляется, — только если других нет.
            var candidates = remaining.Where(m => !m.Deleting).ToList() is { Count: > 0 } alive ? alive : remaining;
            var next = ClanRules.Successor(candidates.Select(m => new ClanMemberInfo(m.UserId, m.Role, m.JoinedAt)))!.Value;
            await db.ClanMembers
                .Where(m => m.UserId == next && m.ClanId == clanId)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.Role, ClanRole.Leader), cancellationToken);
        }

        return true;
    }

    /// <summary>Строка клана — под блокировку (<c>FOR UPDATE</c>) до конца транзакции.</summary>
    internal static Task LockClanAsync(AppDbContext db, Guid clanId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT 1 FROM app.clans WHERE id = {clanId} FOR UPDATE", cancellationToken);

    private static Task<UserEntity?> LockUserAsync(AppDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.Users.FromSql($"SELECT * FROM app.users WHERE id = {userId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(cancellationToken);

    /// <summary>Уже в клане или не прошли 72 ч после выхода — 409 (под блокировкой строки игрока).</summary>
    private static async Task<ProblemHttpResult?> JoinBlockedAsync(
        AppDbContext db, UserEntity user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await db.ClanMembers.AnyAsync(m => m.UserId == user.Id, cancellationToken))
        {
            return Problem(StatusCodes.Status409Conflict, "clan_already_member", "Ты уже в клане — сначала выйди из него.");
        }

        return user.ClanJoinAfter > now
            ? Problem(
                StatusCodes.Status409Conflict,
                "clan_join_cooldown",
                $"После выхода из клана вступить в другой можно через {ClanRules.JoinCooldown.TotalHours:0} ч.")
            : null;
    }

    /// <summary>Своё членство под блокировкой своего клана; <c>null</c> — не в клане.</summary>
    private static async Task<ClanMemberEntity?> LockedOwnAsync(AppDbContext db, Guid? userId, CancellationToken cancellationToken)
    {
        var clanId = await db.ClanMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => (Guid?)m.ClanId).SingleOrDefaultAsync(cancellationToken);
        if (clanId is null)
        {
            return null;
        }

        await LockClanAsync(db, clanId.Value, cancellationToken);
        return await db.ClanMembers.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == userId && m.ClanId == clanId, cancellationToken);
    }

    /// <summary>
    /// Спрашивающий и участник из пути — оба в клане спрашивающего, под блокировкой этого клана. Чужой игрок (в другом клане
    /// или ни в каком) не находится — <c>null</c>: «нет такого» (egor-server.md, раздел 4, п. 5).
    /// </summary>
    private static async Task<(ClanMemberEntity Actor, ClanMemberEntity Target)?> LockedPairAsync(
        AppDbContext db, Guid? actorId, Guid targetId, CancellationToken cancellationToken)
    {
        if (await LockedOwnAsync(db, actorId, cancellationToken) is not { } actor)
        {
            return null;
        }

        var target = await db.ClanMembers.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == targetId && m.ClanId == actor.ClanId, cancellationToken);
        return target is null ? null : (actor, target);
    }

    /// <summary>Новый код-приглашение <c>XXXX-XXXX</c> (алфавит Крокфорда, как у инвайтов), которого ещё нет ни у одного клана.</summary>
    private static async Task<string> NewCodeAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        while (true)
        {
            // Случайность — только RandomNumberGenerator: у Random следующий код угадывается по предыдущим, а код — это пропуск.
            var raw = RandomNumberGenerator.GetString(InviteCodes.Alphabet, 8);
            var code = $"{raw[..4]}-{raw[4..]}";
            if (!await db.Clans.AnyAsync(c => c.InviteCode == code, cancellationToken))
            {
                return code;
            }
        }
    }

    private static ProblemHttpResult NameInvalid() => Problem(
        StatusCodes.Status400BadRequest,
        "clan_name_invalid",
        $"Название — {ClanRules.NameMinLength}–{ClanRules.NameMaxLength} символа: буквы, цифры, пробел, дефис, без мата.");

    private static ProblemHttpResult NameTaken() =>
        Problem(StatusCodes.Status409Conflict, "clan_name_taken", "Такое название уже занято.");

    private static ProblemHttpResult MemberNotFound() =>
        Problem(StatusCodes.Status404NotFound, "clan_member_not_found", "В твоём клане такого участника нет.");

    private static ProblemHttpResult Forbidden() =>
        Problem(StatusCodes.Status403Forbidden, "clan_forbidden", "Не хватает прав в клане.");

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
