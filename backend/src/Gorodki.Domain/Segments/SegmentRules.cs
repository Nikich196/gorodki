using Gorodki.Domain.Config;
using Gorodki.Domain.Leagues;

namespace Gorodki.Domain.Segments;

/// <summary>
/// Числа «Королей участков» (PLAN.md, §3.13). Из плана — ворота 25 м, коридор 30 м, «бег» не быстрее 30 км/ч, «Местная
/// легенда» за 30 дней и длина отрезка 200–800 м; разрыв точек — решено 15.10 (как у тумана, §7.2: 100 м пешком, 200 м на
/// велосипеде).
/// </summary>
/// <remarks>
/// В игровом конфиге этих чисел пока нет: раздел конфига входит в описание API (<c>GameConfig</c> в
/// <c>contracts/openapi.v1.json</c> и его копии в приложении), а приложению они не нужны — проходы засчитывает только
/// сервер. Перенос в конфиг — отдельной правкой контракта вместе с копией описания в приложении
/// (docs/architecture/segments.md, «Что дальше»).
/// </remarks>
public sealed record SegmentRules
{
    /// <summary>Радиус ворот старта и финиша вокруг концов линии, метры.</summary>
    public double GateRadiusMeters { get; init; } = 25;

    /// <summary>Коридор: каждая точка прохода — не дальше этого от линии отрезка, метры.</summary>
    public double CorridorMeters { get; init; } = 30;

    /// <summary>В лиге «Бег» проход со средней скоростью выше этой не засчитывается, км/ч.</summary>
    public double RunMaxKilometersPerHour { get; init; } = 30;

    /// <summary>
    /// Разрыв точек: соседние точки прохода дальше этого друг от друга — где был игрок между ними, неизвестно, проход
    /// рвётся. Тем же числом проверяется, что путь идёт вдоль всей линии, а не срезает её.
    /// </summary>
    public PerLeague<double> MaxGapMeters { get; init; } = new(Run: 100, Bike: 200);

    /// <summary>«Местная легенда» — больше всех проходов за столько последних игровых суток (по Минску).</summary>
    public int LegendDays { get; init; } = 30;

    /// <summary>Длина отрезка, метры: короче и длиннее отрезки не отбираются.</summary>
    public double MinLengthMeters { get; init; } = 200;

    public double MaxLengthMeters { get; init; } = 800;

    public static SegmentRules Default { get; } = new();

    /// <summary>Самая высокая средняя скорость прохода в лиге, м/с; <c>null</c> — без предела (у «Вело» его дают правила лиги).</summary>
    public double? MaxSpeedMetersPerSecond(League league) => league == League.Run ? RunMaxKilometersPerHour / 3.6 : null;
}
