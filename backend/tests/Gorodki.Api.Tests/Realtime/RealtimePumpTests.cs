using System.Threading.Channels;
using Gorodki.Api.Features.Realtime;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Gorodki.Api.Tests.Realtime;

/// <summary>
/// Цикл отправки подсказок (<see cref="RealtimePump"/>) по подставным часам: подсказки за секунду уходят одним сообщением,
/// неудачная отправка цикл не останавливает, остановка сервера завершает его чисто. Доставку через настоящий хаб
/// проверяют интеграционные тесты (<c>RealtimeTests</c>).
/// </summary>
public sealed class RealtimePumpTests
{
    /// <summary>Сколько настоящего времени ждать фоновый цикл — с запасом на медленную машину CI.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly Guid Anna = Guid.CreateVersion7();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hints_within_a_second_go_out_as_one_message()
    {
        var (pump, hints, hub, clock) = Create();
        await pump.StartAsync(Cancel);

        hints.TilesChanged(League.Run, [new TileKey(1, 1)]);
        await clock.WaitForTimerAsync(); // цикл проснулся и ждёт секунду склейки
        clock.Advance(TimeSpan.FromMilliseconds(500));
        hints.TilesChanged(League.Run, [new TileKey(2, 1), new TileKey(1, 1)]);
        hints.FogChanged(Anna);
        hints.FogChanged(Anna);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var first = await hub.NextAsync();
        var second = await hub.NextAsync();

        Assert.Equal(("group:league:run", GameHub.TilesChanged), (first.Target, first.Method));
        Assert.Equal("run", first.Args[0]);
        Assert.Equal([[1, 1], [2, 1]], Assert.IsType<int[][]>(first.Args[1])); // повтор тайла — один раз
        Assert.Equal(($"user:{Anna}", GameHub.FogChanged), (second.Target, second.Method)); // две подсказки — одно сообщение

        // Подсказка позже секунды — уже отдельным сообщением, и до неё ничего лишнего не ушло.
        hints.TilesChanged(League.Bike, [new TileKey(3, 3)]);
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var later = await hub.NextAsync();
        Assert.Equal(("group:league:bike", GameHub.TilesChanged), (later.Target, later.Method));
        await pump.StopAsync(Cancel);
    }

    [Fact]
    public async Task Failed_send_does_not_stop_the_loop()
    {
        var (pump, hints, hub, clock) = Create();
        hub.Failing = "group:league:run"; // соединение с этой группой оборвалось
        await pump.StartAsync(Cancel);

        hints.TilesChanged(League.Run, [new TileKey(1, 1)]);
        hints.FogChanged(Anna);
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var sameBatch = await hub.NextAsync();

        hints.TilesChanged(League.Bike, [new TileKey(2, 2)]);
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var nextBatch = await hub.NextAsync();

        Assert.Equal(($"user:{Anna}", GameHub.FogChanged), (sameBatch.Target, sameBatch.Method)); // остальное из прохода ушло
        Assert.Equal(("group:league:bike", GameHub.TilesChanged), (nextBatch.Target, nextBatch.Method)); // и цикл жив
        await pump.StopAsync(Cancel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopping_the_server_ends_the_loop_cleanly(bool whileCoalescing)
    {
        var (pump, hints, hub, clock) = Create();
        await pump.StartAsync(Cancel);
        // Цикл точно запущен: одна подсказка прошла. Остановка раньше отменила бы сам запуск фоновой службы, а не цикл.
        hints.FogChanged(Anna);
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        await hub.NextAsync();
        if (whileCoalescing)
        {
            hints.FogChanged(Anna);
            await clock.WaitForTimerAsync();
        }

        await pump.StopAsync(Cancel);

        var loop = pump.ExecuteTask;
        Assert.NotNull(loop);
        await Task.WhenAny(loop, Task.Delay(Patience, Cancel)); // отменённый или упавший цикл здесь не бросает — смотрим статус
        Assert.True(loop.IsCompletedSuccessfully, $"цикл завершился так: {loop.Status}");
        Assert.Equal(1, hub.Count); // только первая: подсказка, ждавшая склейки, при остановке не уходит
    }

    private static (RealtimePump Pump, RealtimeHints Hints, RecordingHub Hub, Clock Clock) Create()
    {
        var hints = new RealtimeHints();
        var hub = new RecordingHub();
        var clock = new Clock();
        return (new RealtimePump(hints, hub, clock, NullLogger<RealtimePump>.Instance), hints, hub, clock);
    }

    /// <summary>Подставные часы, которые сообщают, что цикл завёл таймер: сдвигать их раньше — значит проскочить ожидание.</summary>
    private sealed class Clock : FakeTimeProvider
    {
        private readonly SemaphoreSlim _timers = new(0);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _timers.Release();
            return timer;
        }

        public async Task WaitForTimerAsync()
        {
            Assert.True(await _timers.WaitAsync(Patience, Cancel), "цикл так и не стал ждать секунду склейки");
        }
    }

    private sealed record Sent(string Target, string Method, object?[] Args);

    /// <summary>Хаб, который записывает отправленное; отправка адресату <see cref="Failing"/> падает.</summary>
    private sealed class RecordingHub : IHubContext<GameHub>
    {
        private readonly Channel<Sent> _sent = Channel.CreateUnbounded<Sent>();
        private int _count;

        public RecordingHub() => Clients = new Recipients(this);

        public string? Failing { get; set; }

        public int Count => Volatile.Read(ref _count);

        public IHubClients Clients { get; }

        public IGroupManager Groups => throw new NotSupportedException();

        public async Task<Sent> NextAsync()
        {
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            patience.CancelAfter(Patience);
            return await _sent.Reader.ReadAsync(patience.Token);
        }

        /// <summary>Насос шлёт только группе лиги и одному игроку.</summary>
        private sealed class Recipients(RecordingHub hub) : IHubClients
        {
            public IClientProxy All => throw new NotSupportedException();

            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

            public IClientProxy Client(string connectionId) => throw new NotSupportedException();

            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

            public IClientProxy Group(string groupName) => new Proxy(hub, $"group:{groupName}");

            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
                throw new NotSupportedException();

            public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

            public IClientProxy User(string userId) => new Proxy(hub, $"user:{userId}");

            public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
        }

        private sealed class Proxy(RecordingHub hub, string target) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                if (target == hub.Failing)
                {
                    throw new IOException("Соединение оборвалось.");
                }

                Interlocked.Increment(ref hub._count);
                hub._sent.Writer.TryWrite(new Sent(target, method, args));
                return Task.CompletedTask;
            }
        }
    }
}
