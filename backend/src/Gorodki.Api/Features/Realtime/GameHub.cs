using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using Gorodki.Domain.Leagues;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Gorodki.Api.Features.Realtime;

/// <summary>
/// Реальное время (PLAN.md, D6: SignalR): приложение подписывается на лигу и получает подсказки «тайлы изменились»
/// и «заявка решена». Хаб только раздаёт подсказки: в базу не ходит, данных не отдаёт — карта и итоги берутся через REST,
/// с той же публичной задержкой (<see cref="Territory.TerritoryReader.PublicHorizon"/>).
/// </summary>
/// <remarks>
/// Группа — лига целиком, а не тайлы: подписка на тайлы не защищала бы приватность (REST и так отдаёт весь город),
/// но добавляла бы лимиты и повторные подписки; а окно карты — почти местоположение самого зрителя, его лучше не знать.
/// </remarks>
[Authorize]
public sealed class GameHub(HubConnections connections) : Hub
{
    public const string Path = "/hubs/game";

    public const string TilesChanged = "TilesChanged";

    public const string CaptureDecided = "CaptureDecided";

    public static string Group(League league) => $"league:{league.ToString().ToLowerInvariant()}";

    public override async Task OnConnectedAsync()
    {
        if (!connections.TryAdd(Context.UserIdentifier, Context.ConnectionId))
        {
            Context.Abort(); // больше трёх соединений на игрока — лишнее (телефон, веб, запас на переподключение)
            return;
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.UserIdentifier, Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Подписка на лигу («run» или «bike»); прежняя подписка этого соединения снимается.</summary>
    public async Task Subscribe(string league)
    {
        if (!Enum.TryParse<League>(league, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new HubException("league_invalid");
        }

        foreach (var other in Enum.GetValues<League>().Where(l => l != parsed))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(other));
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(parsed));
    }
}

/// <summary>Кто вошёл: номер игрока — claim <c>sub</c> (стандартный ищет NameIdentifier, которого в нашем токене нет).</summary>
public sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
}

/// <summary>Соединения игроков с хабом — чтобы у одного игрока их было не больше <see cref="MaxPerUser"/>.</summary>
public sealed class HubConnections
{
    public const int MaxPerUser = 3;

    private readonly ConcurrentDictionary<string, HashSet<string>> _byUser = new();

    public bool TryAdd(string? userId, string connectionId)
    {
        if (userId is null)
        {
            return false;
        }

        var set = _byUser.GetOrAdd(userId, _ => []);
        lock (set)
        {
            if (set.Count >= MaxPerUser)
            {
                return false;
            }

            set.Add(connectionId);
            return true;
        }
    }

    public void Remove(string? userId, string connectionId)
    {
        if (userId is not null && _byUser.TryGetValue(userId, out var set))
        {
            lock (set)
            {
                set.Remove(connectionId);
            }
        }
    }
}
