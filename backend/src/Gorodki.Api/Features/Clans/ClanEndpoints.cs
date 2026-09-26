using System.Security.Claims;
using System.Text.Json.Serialization;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.Clans;

/// <summary>Роль в клане (PLAN.md, §3.6: «лидер и до 2 офицеров»).</summary>
public enum ClanRole
{
    Member,
    Officer,
    Leader,
}

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
/// исключение, роли; E5c — переименование, оттенки, новый код (docs/guides/egor-server.md, раздел 7).
/// </summary>
/// <remarks>
/// Действия над своим кланом — по адресам <c>/clans/mine/…</c>: номер клана в них не нужен, чужой клан так не тронуть.
/// Номер участника в пути (<c>/clans/mine/members/{userId}</c>) ищется только в своём клане — чужой игрок: 404.
/// </remarks>
public static class ClanEndpoints
{
    /// <summary>Потолок участников (§3.6).</summary>
    public const int MaxMembers = 12;

    /// <summary>С этого числа участников клан «полный» (§3.6).</summary>
    public const int FullFrom = 3;

    /// <summary>Офицеров — не больше (§3.6).</summary>
    public const int MaxOfficers = 2;

    /// <summary>Оттенков в палитре кланов (§3.6).</summary>
    public const int Hues = 12;

    /// <summary>После выхода или исключения — столько без вступления в другой клан (§3.3).</summary>
    public static readonly TimeSpan JoinCooldown = TimeSpan.FromHours(72);

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

    private static Task<Results<Created<ClanResponse>, ProblemHttpResult>> CreateClan(
        CreateClanRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #135 (Егор, E5a): новые таблицы ClanEntity (имя, нормализованное имя, оттенок, код, лидер, сезон
        // переименования) и ClanMemberEntity (роль, дата), у игрока — «можно вступать с»; миграция (egor-server.md, 2.4).
        // Правила имени — чистая функция Gorodki.Domain/Clans/ClanRules.cs. Строку игрока — под блокировку (FOR UPDATE, как
        // PrivacyZoneEndpoints.Create): два одновременных запроса не создадут два клана. Код — как InviteCodes.New().
        // Ответ — 201 и ClanResponse (создатель — Leader). Тесты — ClansTests.
        _ = (request, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #135");
    }

    private static Task<Results<Ok<MyClanResponse>, NotFound>> GetMyClan(
        ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #135 (Егор, E5a): свой клан (ClanResponse, как в GetClan) или Clan = null и CanJoinAtMs — пока идут 72 ч
        // после выхода или исключения. Игрока нет — 404. Тесты — ClansTests.
        _ = (principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #135");
    }

    private static Task<Ok<ClanHuesResponse>> GetClanHues(AppDbContext db, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #135 (Егор, E5c): оттенки 0–11, которых нет ни у одного клана. Строгой уникальности «в Арене» пока нет —
        // она появится после границ Арены (egor-server.md, карточка E5). Тесты — ClansTests.
        _ = (db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #135");
    }

    private static Task<Results<Ok<ClanResponse>, ProblemHttpResult>> GetClan(
        Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #135 (Егор, E5a): клан и участники. Имя участника — правило карточки игрока (#115): ник, если согласие
        // (PublicProfile) или это сам спрашивающий, иначе LeaderboardEndpoints.Pseudonym(id). Аккаунт удаляется
        // (DeletionRequestedAt) — участника не показывать. InviteCode — только если спрашивающий здесь лидер или офицер.
        // Нет клана — 404 clan_not_found. Тесты — ClansTests; строка в IdorTests.AwaitingTasks.
        _ = (id, principal, db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #135");
    }

    private static Task<Results<Ok<ClanResponse>, ProblemHttpResult>> JoinClan(
        JoinClanRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #135 (Егор, E5a): код → клан (нет — 404 clan_code_invalid); потолок 12 — проверять под блокировкой строки
        // клана, иначе двое одновременно вступят тринадцатым. Уже в клане, 72 ч не прошли — 409 (коды — в описании адреса).
        // Тесты — ClansTests.
        _ = (request, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #135");
    }

    private static Task<Results<NoContent, ProblemHttpResult>> LeaveClan(
        ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #135 (Егор, E5b): убрать членство, «можно вступать с» = сейчас + 72 ч. Ушёл лидер — лидер: офицер (самый
        // давний из офицеров), иначе самый давний участник. Ушёл последний — клана больше нет (уточнит Никита, если нужно
        // иначе). Землю не трогать. Тесты — ClansTests.
        _ = (principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #135");
    }

    private static Task<Results<NoContent, ProblemHttpResult>> RemoveClanMember(
        Guid userId, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #135 (Егор, E5b): участник ищется в клане спрашивающего одним запросом (чужой — 404 clan_member_not_found,
        // egor-server.md, раздел 4, п. 5). Лидер исключает любого, офицер — рядового (кто кого — уточнит Никита), иначе 403
        // clan_forbidden. Исключённому — 72 ч без вступления. Тесты — ClansTests; строка в IdorTests.AwaitingTasks.
        _ = (userId, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #135");
    }

    private static Task<Results<Ok<ClanResponse>, ProblemHttpResult>> SetClanMemberRole(
        Guid userId, ClanRoleRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #135 (Егор, E5b): только лидер; роль officer или member; офицеров не больше 2 (под блокировкой клана).
        // Участник — в своём клане, иначе 404 clan_member_not_found. Ответ — клан. Тесты — ClansTests; строка в
        // IdorTests.AwaitingTasks.
        _ = (userId, request, principal, db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #135");
    }

    private static Task<Results<Ok<ClanResponse>, ProblemHttpResult>> RenameClan(
        RenameClanRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #135 (Егор, E5c): только лидер, раз в сезон (сезон — SeasonStore.CalendarAsync(…).At(now)); правила имени
        // — ClanRules. Спорное название по жалобе переименовывает админ (§3.6) — это не здесь. Тесты — ClansTests.
        _ = (request, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #135");
    }

    private static Task<Results<Ok<ClanResponse>, ProblemHttpResult>> NewClanCode(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        // ЗАДАЧА #135 (Егор, E5c): лидер или офицер; новый код (как InviteCodes.New()), старый больше не действует. Ответ —
        // клан с новым кодом. Тесты — ClansTests.
        _ = (principal, db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #135");
    }
}
