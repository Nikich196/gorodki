using System.Collections.Concurrent;
using System.Net;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Realtime;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Реальное время на настоящей базе (PLAN.md, D6, §3.16): автор узнаёт итог заявки сразу, остальные о чужом захвате —
/// только когда он стал публичным; подсказки идут только вошедшим.
/// </summary>
/// <remarks>
/// Отправку подсказок тесты делают сами (<see cref="RealtimePump.DrainAsync"/>), а «ничего не пришло» проверяют барьером:
/// после отправки сервер шлёт этому соединению «Barrier», и порядок внутри соединения гарантирован — ждать по таймауту не нужно.
/// Перед сдвигом подставных часов соединения закрываются: таймауты SignalR могут идти по тем же часам.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class RealtimeTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hub_needs_sign_in()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);

        await using var anonymous = Connection(api, token: null);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => anonymous.StartAsync(Cancel));
    }

    [Fact]
    public async Task Author_hears_the_decision_at_once_and_others_only_when_the_capture_becomes_public()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await RevealsAsync(api); // граница публичности запомнена

        var annaHub = await ListenAsync(api, anna);
        var veraHub = await ListenAsync(api, vera);
        var claim = await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        await Walks.ProcessAsync(api, claim.RunId);
        await DrainAsync(api);
        await annaHub.BarrierAsync(api, Cancel);
        await veraHub.BarrierAsync(api, Cancel);

        Assert.Equal((claim.RunId, claim.CaptureId, "applied"), Assert.Single(annaHub.Decided));
        Assert.Contains(annaHub.Tiles, t => t.League == "run" && t.Tiles.Contains((tile.X, tile.Y))); // свой захват — сразу
        Assert.Empty(veraHub.Tiles); // чужой ещё скрыт — ни слова
        Assert.Empty(veraHub.Decided);
        await annaHub.DisposeAsync();
        await veraHub.DisposeAsync();

        api.Time.Advance(TerritoryReader.PublicDelay + TerritoryReader.RevealStep);
        var veraLater = await ListenAsync(api, vera);
        Assert.True(await RevealsAsync(api) >= 1);
        await DrainAsync(api);
        await veraLater.BarrierAsync(api, Cancel);

        Assert.Contains(veraLater.Tiles, t => t.League == "run" && t.Tiles.Contains((tile.X, tile.Y)));
        await veraLater.DisposeAsync();
    }

    [Fact]
    public async Task Rejected_claim_is_reported_to_its_author_only()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var annaHub = await ListenAsync(api, anna);
        var veraHub = await ListenAsync(api, vera);

        var claim = await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100), motionAuthorized: false);
        await Walks.ProcessAsync(api, claim.RunId);
        await DrainAsync(api);
        await annaHub.BarrierAsync(api, Cancel);
        await veraHub.BarrierAsync(api, Cancel);

        Assert.Equal((claim.RunId, claim.CaptureId, "rejected"), Assert.Single(annaHub.Decided));
        Assert.Empty(annaHub.Tiles); // земля не менялась
        Assert.Empty(veraHub.Decided);
        await annaHub.DisposeAsync();
        await veraHub.DisposeAsync();
    }

    [Fact]
    public async Task New_fog_is_hinted_to_its_owner_only_and_only_when_it_changed()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var annaHub = await ListenAsync(api, anna);
        var veraHub = await ListenAsync(api, vera);
        var area = NewArea();

        var first = await WalkAndFinishAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        Assert.True(await StampAsync(api, first.Id) > 0);
        await DrainAsync(api);
        await annaHub.BarrierAsync(api, Cancel);
        await veraHub.BarrierAsync(api, Cancel);
        Assert.Equal(1, annaHub.FogChanged);
        Assert.Equal(0, veraHub.FogChanged);

        // Тот же путь ещё раз — нового тумана нет, подсказки тоже.
        api.Time.Advance(TimeSpan.FromMinutes(30));
        var again = await WalkAndFinishAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        Assert.Equal(0, await StampAsync(api, again.Id));
        await DrainAsync(api);
        await annaHub.BarrierAsync(api, Cancel);
        Assert.Equal(1, annaHub.FogChanged);
        await annaHub.DisposeAsync();
        await veraHub.DisposeAsync();
    }

    [Fact]
    public async Task Cleared_fog_is_hinted_to_its_owner_only_and_only_when_something_was_erased()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var walk = await WalkAndFinishAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100));
        Assert.True(await StampAsync(api, walk.Id) > 0);
        await DrainAsync(api); // подсказка о самом забеге — до подключения, её никто не слышит
        var annaHub = await ListenAsync(api, anna);
        var veraHub = await ListenAsync(api, vera);

        Assert.Equal(HttpStatusCode.NoContent, (await anna.DeleteAsync("/fog", Cancel)).StatusCode);
        await DrainAsync(api);
        await annaHub.BarrierAsync(api, Cancel);
        await veraHub.BarrierAsync(api, Cancel);
        Assert.Equal(1, annaHub.FogChanged); // телефон перезапросит тайлы и получит их пустыми
        Assert.Equal(0, veraHub.FogChanged);

        // Стирать уже нечего — и подсказки нет.
        Assert.Equal(HttpStatusCode.NoContent, (await anna.DeleteAsync("/fog", Cancel)).StatusCode);
        await DrainAsync(api);
        await annaHub.BarrierAsync(api, Cancel);
        Assert.Equal(1, annaHub.FogChanged);
        await annaHub.DisposeAsync();
        await veraHub.DisposeAsync();
    }

    [Fact]
    public async Task Demo_account_captures_reach_everyone_at_once()
    {
        // PLAN.md, §11, показ: «захват точно по контуру, /live обновился».
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (demo, _) = await api.CreatePlayerClientAsync(UserRole.Demo);
        var (vera, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var veraHub = await ListenAsync(api, vera);

        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, demo, Square(area, 0, 0, 100))).RunId);
        await DrainAsync(api);
        await veraHub.BarrierAsync(api, Cancel);

        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        Assert.Contains(veraHub.Tiles, t => t.Tiles.Contains((tile.X, tile.Y)));
        await veraHub.DisposeAsync();
    }

    [Fact]
    public async Task A_player_holds_at_most_three_connections()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var hubs = new List<Listener>();
        for (var i = 0; i < HubConnections.MaxPerUser; i++)
        {
            hubs.Add(await ListenAsync(api, anna));
        }

        var extra = Connection(api, Token(anna));
        var closed = new TaskCompletionSource();
        extra.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        try
        {
            await extra.StartAsync(Cancel);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            closed.TrySetResult(); // сервер оборвал соединение ещё до конца подключения — тоже отказ
        }

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancel); // четвёртое соединение сервер закрыл
        Assert.All(hubs, h => Assert.Equal(HubConnectionState.Connected, h.Connection.State));
        foreach (var hub in hubs)
        {
            await hub.DisposeAsync();
        }

        await extra.DisposeAsync();
    }

    // MARK: — вспомогательное

    private static string? Token(HttpClient client) => client.DefaultRequestHeaders.Authorization?.Parameter;

    /// <summary>Соединение с хабом через тестовый сервер (LongPolling — единственный транспорт, который TestServer отдаёт как есть).</summary>
    private static HubConnection Connection(ApiFactory api, string? token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(api.Server.BaseAddress, GameHub.Path.TrimStart('/')), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(token);
            })
            .Build();

    private async Task<Listener> ListenAsync(ApiFactory api, HttpClient client)
    {
        var listener = new Listener(Connection(api, Token(client)));
        await listener.Connection.StartAsync(Cancel);
        await listener.Connection.InvokeAsync("Subscribe", "run", Cancel);
        return listener;
    }

    private static async Task<int?> StampAsync(ApiFactory api, Guid runId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FogProcessor>().StampRunAsync(runId, CancellationToken.None);
    }

    private static async Task DrainAsync(ApiFactory api) =>
        await api.Services.GetRequiredService<RealtimePump>().DrainAsync(CancellationToken.None);

    private static async Task<int> RevealsAsync(ApiFactory api) =>
        await api.Services.GetRequiredService<RevealScanner>().RunOnceAsync(CancellationToken.None);

    /// <summary>Что услышало соединение.</summary>
    private sealed class Listener : IAsyncDisposable
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _barriers = new();
        private int _fogChanged;

        /// <summary>Сколько раз пришла подсказка «туман изменился».</summary>
        public int FogChanged => Volatile.Read(ref _fogChanged);

        public Listener(HubConnection connection)
        {
            Connection = connection;
            connection.On<string, int[][]>(GameHub.TilesChanged, (league, tiles) =>
                Tiles.Add((league, [.. tiles.Select(t => (t[0], t[1]))])));
            connection.On<Guid, Guid, string>(GameHub.CaptureDecided, (runId, captureId, status) =>
                Decided.Add((runId, captureId, status)));
            connection.On(GameHub.FogChanged, () => Interlocked.Increment(ref _fogChanged));
            connection.On("Barrier", () =>
            {
                if (_barriers.TryDequeue(out var barrier))
                {
                    barrier.TrySetResult();
                }
            });
        }

        public HubConnection Connection { get; }

        public ConcurrentBag<(string League, List<(int X, int Y)> Tiles)> Tiles { get; } = [];

        public ConcurrentBag<(Guid RunId, Guid CaptureId, string Status)> Decided { get; } = [];

        /// <summary>Всё, что сервер отправил этому соединению раньше, уже получено.</summary>
        public async Task BarrierAsync(ApiFactory api, CancellationToken cancellationToken)
        {
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _barriers.Enqueue(barrier);
            var hub = api.Services.GetRequiredService<IHubContext<GameHub>>();
            await hub.Clients.Client(Connection.ConnectionId!).SendAsync("Barrier", cancellationToken);
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
