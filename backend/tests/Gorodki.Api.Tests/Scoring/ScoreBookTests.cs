using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Scoring;
using Gorodki.Api.Features.Territory;
using Gorodki.Domain.Config;
using Gorodki.Domain.Time;

namespace Gorodki.Api.Tests.Scoring;

/// <summary>
/// Закрытие сезона без базы (docs/architecture/scoring-and-seasons.md, «Итог сезона»): до него очки сезона ещё приходят,
/// после — итог не меняется (на настоящей базе — <c>ScoringTests</c> в <c>Gorodki.IntegrationTests</c>).
/// </summary>
public sealed class ScoreBookTests
{
    /// <summary>Начало Сезона 1 — 30.11 00:00 по Минску; конец Сезона 0.</summary>
    private static readonly DateTimeOffset SeasonOne = new(2026, 11, 29, 21, 0, 0, TimeSpan.Zero);

    private static readonly SeasonCalendar Calendar = new(
    [
        new Season(0, "Сезон 0 (бета)", SeasonOne.AddDays(-14)),
        new Season(1, "Сезон 1", SeasonOne),
    ]);

    [Fact]
    public void Season_closes_at_four_in_the_morning_of_the_next_season_and_the_last_one_never_closes()
    {
        var closesAt = ScoreBook.ClosesAt(Calendar, 0);

        Assert.Equal(SeasonOne + ScoreBook.CloseGrace, closesAt);
        Assert.Equal(new TimeSpan(4, 0, 0), GameClock.ToMinsk(closesAt!.Value).TimeOfDay);
        Assert.Null(ScoreBook.ClosesAt(Calendar, 1)); // последний сезон идёт до показа
        Assert.Null(ScoreBook.ClosesAt(Calendar, 2));
        Assert.Null(ScoreBook.ClosesAt(Calendar, SeasonCalendar.AllTime));
    }

    [Fact]
    public void Close_grace_covers_the_latest_capture_that_still_counts_and_the_visits_of_the_last_run()
    {
        // Петля в последнюю миллисекунду сезона: применяется, пока ей не больше StaleAfter от прихода данных, и видна с
        // границы публичности применения — до закрытия. С любой задержкой публичности из конфига по умолчанию.
        var delay = TimeSpan.FromMinutes(GameConfig.Default.Privacy.PublicEventDelayMinutes);
        var latestApplied = SeasonOne + CaptureProcessor.StaleAfter;

        Assert.True(TerritoryReader.PublicAt(latestApplied, delay) < SeasonOne + ScoreBook.CloseGrace);
        Assert.True(TerritoryReader.PublicAt(SeasonOne, delay) < SeasonOne + ScoreBook.CloseGrace); // визиты забега до полуночи
    }
}
