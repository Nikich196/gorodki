using Gorodki.Api.Features.Captures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gorodki.Api.Tests.Captures;

/// <summary>
/// Изоляция фонового обработчика: испорченный забег или упавшая стадия не останавливают остальной проход
/// (на настоящей базе весь проход проверяет <c>WorkerIsolationTests</c>).
/// </summary>
public sealed class CaptureWorkerTests
{
    private static readonly Guid First = Guid.CreateVersion7();
    private static readonly Guid Poisoned = Guid.CreateVersion7();
    private static readonly Guid Last = Guid.CreateVersion7();

    [Fact]
    public async Task Failing_run_is_postponed_and_the_runs_after_it_are_still_processed()
    {
        var processed = new List<Guid>();
        var postponed = new List<Guid>();

        var failed = await CaptureWorker.EachRunAsync(
            "туман",
            [First, Poisoned, Last],
            id => id == Poisoned ? throw new FormatException("Кусок забега обрезан.") : Record(processed, id),
            id => Record(postponed, id),
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, failed);
        Assert.Equal([First, Last], processed);
        Assert.Equal([Poisoned], postponed);
    }

    [Fact]
    public async Task Run_that_cannot_even_be_postponed_does_not_stop_the_stage()
    {
        var processed = new List<Guid>();

        var failed = await CaptureWorker.EachRunAsync(
            "визиты",
            [Poisoned, Last],
            id => id == Poisoned ? throw new InvalidOperationException("сбой") : Record(processed, id),
            _ => throw new InvalidOperationException("база недоступна"),
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, failed);
        Assert.Equal([Last], processed);
    }

    [Fact]
    public async Task Failed_stage_does_not_stop_the_stages_after_it()
    {
        var ran = new List<string>();

        await CaptureWorker.StageAsync("захваты", () => throw new InvalidOperationException("сбой"), NullLogger.Instance, CancellationToken.None);
        await CaptureWorker.StageAsync("откаты", () => { ran.Add("откаты"); return Task.CompletedTask; }, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(["откаты"], ran);
    }

    [Fact]
    public async Task Stopping_the_server_is_not_taken_for_a_failed_run()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        var postponed = new List<Guid>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CaptureWorker.EachRunAsync(
            "туман",
            [First],
            _ => throw new OperationCanceledException(stopping.Token),
            id => Record(postponed, id),
            NullLogger.Instance,
            stopping.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CaptureWorker.StageAsync(
            "туман", () => throw new OperationCanceledException(stopping.Token), NullLogger.Instance, stopping.Token));
        Assert.Empty(postponed);
    }

    [Fact]
    public async Task Cancellation_that_is_not_a_shutdown_is_an_ordinary_failure()
    {
        // Например, отменённый изнутри запрос: сервер не останавливается — значит, это ошибка забега, а не выход.
        var postponed = new List<Guid>();

        var failed = await CaptureWorker.EachRunAsync(
            "туман",
            [Poisoned],
            _ => throw new TaskCanceledException(),
            id => Record(postponed, id),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(1, failed);
        Assert.Equal([Poisoned], postponed);
    }

    [Fact]
    public async Task Every_stage_of_the_pass_and_of_the_hourly_cleanup_is_tried_even_if_each_one_fails()
    {
        // База недоступна — падает каждая стадия, но пробует каждая: ошибка одной не отменяет следующие.
        await using var app = UnreachableDatabase.Server();
        var log = new StageLog();
        var worker = ActivatorUtilities.CreateInstance<CaptureWorker>(app.Services, log);

        await worker.RunPassAsync(TestContext.Current.CancellationToken);
        await worker.HousekeepingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["захваты", "визиты", "туман", "раскрытия", "откаты",
                "журнал захватов", "забытые забеги", "сырые точки", "токены входа", "удаление аккаунтов", "срез рейтингов"],
            log.FailedStages);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    [InlineData(7, 60)]
    [InlineData(1_000, 60)]
    public void Retry_pause_doubles_from_a_minute_up_to_an_hour(int failures, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), CaptureWorker.RetryDelay(failures));
    }

    private static Task Record(List<Guid> list, Guid id)
    {
        list.Add(id);
        return Task.CompletedTask;
    }

    /// <summary>Лог обработчика: какие стадии не завершились — по порядку.</summary>
    private sealed class StageLog : ILogger<CaptureWorker>
    {
        public List<string> FailedStages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            if (logLevel == LogLevel.Error
                && fields.Any(f => f.Key == "{OriginalFormat}" && Equals(f.Value, "Стадия «{Stage}» фонового обработчика не завершилась")))
            {
                FailedStages.Add((string)fields.Single(f => f.Key == "Stage").Value!);
            }
        }
    }
}
