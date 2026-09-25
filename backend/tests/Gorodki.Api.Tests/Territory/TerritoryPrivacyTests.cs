using Gorodki.Api.Features.Territory;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gorodki.Api.Tests.Territory;

/// <summary>Публичная проекция карты (PLAN.md, §3.16): граница раскрытия и номера кусков от содержимого.</summary>
public sealed class TerritoryPrivacyTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMinutes(20);

    [Theory]
    [InlineData("2026-11-16T12:24:59Z", "2026-11-16T12:00:00Z")]
    [InlineData("2026-11-16T12:25:00Z", "2026-11-16T12:05:00Z")]
    [InlineData("2026-11-16T12:29:59.999Z", "2026-11-16T12:05:00Z")]
    public void Changes_become_public_in_five_minute_batches(string now, string horizon)
    {
        // Захват в 12:01 и захват в 12:04 раскрываются вместе, в 12:25: минута захвата не видна.
        Assert.Equal(DateTimeOffset.Parse(horizon), TerritoryReader.PublicHorizon(DateTimeOffset.Parse(now), Delay));
    }

    [Theory]
    [InlineData(20, 180)] // по умолчанию — три часа: 2 × (20 + 5) = 50 минут, меньше нижней границы
    [InlineData(5, 180)]
    [InlineData(40, 180)]
    [InlineData(100, 210)] // дольше задержка — дольше хранение: 2 × (100 + 5)
    public void Exact_undo_rows_outlive_every_capture_that_can_still_be_hidden(int delayMinutes, int retentionMinutes)
    {
        // Строки точного отката нужны, пока захват скрыт: не дольше задержки плюс шаг раскрытия. Стереть раньше — проекция
        // скрытого захвата уйдёт в запасной путь (с изломами и швом); чистка раз в час, поэтому запас вдвое.
        var delay = TimeSpan.FromMinutes(delayMinutes);

        var retention = Gorodki.Api.Features.Captures.CaptureProcessor.ExactUndoRetention(delay);

        Assert.Equal(TimeSpan.FromMinutes(retentionMinutes), retention);
        Assert.True(retention >= 2 * (delay + TerritoryReader.RevealStep));
        var appliedAt = DateTimeOffset.Parse("2026-11-16T12:04:59Z"); // худший случай — перед границей шага
        var revealedAt = appliedAt + delay + TerritoryReader.RevealStep;
        Assert.True(TerritoryReader.PublicHorizon(revealedAt, delay) >= appliedAt); // к этому моменту захват уже публичен
        Assert.True(appliedAt + retention > revealedAt);
    }

    [Theory]
    [InlineData(20, 60)]
    [InlineData(20, 175)] // до 2 ч 55 мин — без потерь
    [InlineData(5, 175)]
    public void Raising_the_delay_does_not_hide_captures_whose_exact_undo_rows_are_already_gone(int oldMinutes, int newMinutes)
    {
        // Срок хранения — по задержке на момент чистки, скрытость — по задержке на момент чтения. Администратор поднял
        // задержку: захваты, уже публичные по прежней, снова скрыты. Их строки точного отката должны ещё лежать — иначе
        // проекция уйдёт в запасной путь (изломы и шов, аудит BE-01). Хуже всего — сразу после чистки по прежнему сроку.
        var prunedAt = DateTimeOffset.Parse("2026-11-16T12:00:00Z");
        var keptSince = prunedAt - Gorodki.Api.Features.Captures.CaptureProcessor.ExactUndoRetention(TimeSpan.FromMinutes(oldMinutes));

        // Самый старый захват, который скрыт при новой задержке, — граница публичности вниз до шага (5 минут).
        var hiddenSince = TerritoryReader.PublicHorizon(prunedAt, TimeSpan.FromMinutes(newMinutes));

        Assert.True(hiddenSince >= keptSince, $"скрыты захваты с {hiddenSince:HH:mm}, а строки лежат только с {keptSince:HH:mm}");
    }

    [Fact]
    public void Projection_log_line_counts_fallbacks_by_reason_and_has_no_places()
    {
        var stats = new ProjectionStats();
        stats.Add(new Gorodki.Domain.Territory.TileProjection([], [Gorodki.Domain.Territory.UndoPath.Exact, Gorodki.Domain.Territory.UndoPath.Missing], []));
        stats.Add(new Gorodki.Domain.Territory.TileProjection(
            [], [Gorodki.Domain.Territory.UndoPath.Exception, Gorodki.Domain.Territory.UndoPath.Exact], [new FormatException("684:5775")]));
        stats.AddEmptyTile();

        Assert.Equal((2, 2, 1), (stats.Exact, stats.Fallback, stats.EmptyTiles));
        Assert.Equal("точно 2, запасным путём 2 (missing 1, exception 1, FormatException 1), пустых тайлов 1", stats.Describe());
    }

    [Fact]
    public void Projection_log_line_is_information_only_when_something_fell_back_or_came_out_empty()
    {
        // Карту перечитывают часто (подсказки /live, опрос): строка на каждое точное чтение — шум. Видеть в проде надо
        // запасной путь и пустые тайлы.
        var exact = new ProjectionStats();
        exact.Add(new TileProjection([], [UndoPath.Exact, UndoPath.Exact], []));
        var fallback = new ProjectionStats();
        fallback.Add(new TileProjection([], [UndoPath.Exact, UndoPath.NoRows], []));
        var empty = new ProjectionStats();
        empty.AddEmptyTile();

        Assert.Equal((LogLevel.Debug, LogLevel.Information, LogLevel.Information), (exact.Level, fallback.Level, empty.Level));
    }

    [Fact]
    public void Any_failure_of_the_projection_step_gives_an_empty_tile_instead_of_an_error()
    {
        // Запасной путь — это весь движок и NTS: любая неожиданная ошибка расчёта (здесь — ArgumentException, какой NTS
        // отвечает на вырожденную геометрию) не должна ронять чтение карты. 500 получал бы каждый, кто смотрит тайл, до
        // конца скрытия, а 500 ровно на тайлах со скрытым захватом показал бы, где он.
        var stats = new ProjectionStats();
        var log = new ListLogger();
        var failure = new ArgumentException("Invalid number of points in LinearRing (found 2 - must be 0 or >= 3)");

        var pieces = TerritoryReader.ProjectTile(
            new TileKey(684, 5775), [], [new HiddenTileChange(null, () => throw failure)], new TerritoryRules(), stats, log);

        Assert.Empty(pieces);
        Assert.Equal((0, 0, 1), (stats.Exact, stats.Fallback, stats.EmptyTiles));
        Assert.Equal((LogLevel.Error, (Exception?)failure), Assert.Single(log.Entries));
    }

    [Fact]
    public void Cancelled_projection_step_is_not_passed_off_as_an_empty_tile()
    {
        var stats = new ProjectionStats();

        Assert.Throws<OperationCanceledException>(() => TerritoryReader.ProjectTile(
            new TileKey(684, 5775),
            [],
            [new HiddenTileChange(null, () => throw new OperationCanceledException())],
            new TerritoryRules(),
            stats,
            NullLogger.Instance));
        Assert.True(stats.IsEmpty);
    }

    /// <summary>Журнал сервера в памяти: уровень и исключение каждой строки.</summary>
    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Error)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }

    [Fact]
    public void Same_content_gives_the_same_id_and_any_visible_change_a_new_one()
    {
        var tile = new TileKey(684, 5775);
        var piece = new ParcelView(
            0, Guid.Parse("00000000-0000-0000-0000-00000000000a"), 3, 1, false, 1_790_000_000_000, null, null,
            [52.0976, 23.688, 52.0976, 23.6895, 52.0985, 23.6895, 52.0976, 23.688], []);

        var id = TerritoryReader.ContentId(tile, piece);

        Assert.Equal(id, TerritoryReader.ContentId(tile, piece with { Id = 99 })); // сам номер в нём не участвует
        Assert.NotEqual(id, TerritoryReader.ContentId(tile, piece with { Level = 2 }));
        Assert.NotEqual(id, TerritoryReader.ContentId(tile, piece with { ShieldUntilMs = 1_790_043_200_000 }));
        Assert.NotEqual(id, TerritoryReader.ContentId(new TileKey(684, 5776), piece));
        Assert.InRange(id, 0, (1L << 53) - 1); // точно представим в JavaScript
    }
}
