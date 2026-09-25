using System.Security.Claims;
using System.Text.Json.Serialization;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gorodki.Api.Features.Admin;

/// <summary>Выпустить инвайт-коды.</summary>
public sealed record CreateInvitesRequest
{
    /// <summary>Сколько кодов выпустить: от 1 до <see cref="InviteEndpoints.MaxCount"/>.</summary>
    [JsonRequired]
    public required int Count { get; init; }

    /// <summary>Сколько игроков может зарегистрироваться по каждому коду: от 1 до <see cref="InviteEndpoints.MaxUsesLimit"/>.</summary>
    [JsonRequired]
    public required int MaxUses { get; init; }

    /// <summary>До какого момента коды действуют (мс Unix, только в будущем); <c>null</c> — бессрочно.</summary>
    public long? ExpiresAtMs { get; init; }

    /// <summary>Пометка для себя, например «группа ПО-4»: до <see cref="InviteEndpoints.MaxNoteLength"/> символов.</summary>
    public string? Note { get; init; }
}

/// <summary>Инвайт-код и сколько игроков по нему уже зарегистрировалось.</summary>
/// <param name="ExpiresAtMs">До какого момента код действует (мс Unix); <c>null</c> — бессрочно. Погашенный — в прошлом.</param>
/// <param name="CreatedAtMs">Когда выпущен (мс Unix).</param>
public sealed record InviteResponse(string Code, int MaxUses, int UsedCount, long? ExpiresAtMs, string? Note, long CreatedAtMs);

/// <summary>
/// Админка инвайтов (PLAN.md, D10: регистрация закрытая, Сезоны 0–2): выпустить коды, посмотреть, сколько по каждому пришло,
/// погасить утёкший. Часть адресов администратора (<see cref="AdminEndpoints"/>): в описание API для приложения не входят,
/// роль проверяется по базе при каждом запросе (<see cref="AdminEndpoints.AdminIdAsync"/>). Код расходуется при регистрации —
/// <c>AuthEndpoints.SignInWithGoogle</c>.
/// </summary>
public static class InviteEndpoints
{
    public const int MaxCount = 50;

    public const int MaxUsesLimit = 20;

    public const int MaxNoteLength = 200;

    public static RouteGroupBuilder MapInviteEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapPost("/invites", CreateInvites);
        admin.MapGet("/invites", ListInvites);
        admin.MapDelete("/invites/{code}", RevokeInvite);
        return admin;
    }

    /// <summary>Выпустить <c>count</c> новых кодов. Ответ — 201 и список выпущенных.</summary>
    private static Task<Results<Created<IReadOnlyList<InviteResponse>>, ProblemHttpResult>> CreateInvites(
        CreateInvitesRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        // ЗАДАЧА #114 (Егор): только админ (AdminEndpoints.AdminIdAsync; иначе 403 admin_only). Проверить Count (1–50),
        // MaxUses (1–20), ExpiresAtMs (если есть — позже «сейчас»), Note (до 200 символов) — иначе 400 invites_invalid, и
        // ничего не создавать. Коды — InviteCodes.New(); совпал с уже выданным — взять другой. Сохранить InviteEntity
        // (CreatedAt — по часам time) и вернуть 201 со списком: TypedResults.Created<IReadOnlyList<InviteResponse>>(…) —
        // тип в скобках обязателен, иначе CS0029 (egor-server.md, 6.2). Тесты — AdminInvitesTests.
        _ = (request, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #114");
    }

    /// <summary>Все коды, новые сверху, с числом использований.</summary>
    private static Task<Results<Ok<IReadOnlyList<InviteResponse>>, ProblemHttpResult>> ListInvites(
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        // ЗАДАЧА #114 (Егор): только админ; все InviteEntity, новые сверху (CreatedAt по убыванию). Ответ —
        // TypedResults.Ok<IReadOnlyList<InviteResponse>>(…), тип в скобках обязателен. Тесты — AdminInvitesTests.
        _ = (principal, db, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #114");
    }

    /// <summary>Погасить код: по нему больше никто не зарегистрируется. Строка остаётся — по ней видно, кто по какому коду пришёл.</summary>
    private static Task<Results<NoContent, ProblemHttpResult>> RevokeInvite(
        string code,
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        // ЗАДАЧА #114 (Егор): только админ; кода нет — 404 invite_not_found. Иначе ExpiresAt = «сейчас» (уже погашенный —
        // не трогать) и 204. Строку не удалять: у игроков в users.invite_code записан код, по которому они пришли.
        // Тесты — AdminInvitesTests.
        _ = (code, principal, db, time, cancellationToken);
        throw new NotImplementedException("ЗАДАЧА #114");
    }
}
