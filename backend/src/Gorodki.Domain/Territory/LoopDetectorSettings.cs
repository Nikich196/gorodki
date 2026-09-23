namespace Gorodki.Domain.Territory;

/// <summary>
/// Числа детектора петли на телефоне (PLAN.md, §3.2). Зеркало <c>LoopDetectorSettings</c> из GameCore (Swift);
/// сервер проверяет заявку теми же R и минимальным путём.
/// </summary>
public sealed record LoopDetectorSettings
{
    /// <summary>Минимальный путь от начала петли до её замыкания, метры.</summary>
    public double MinPathMeters { get; init; } = 150;

    /// <summary>R = clamp(1,62·√(accᵢ² + accₙ²), MinRadiusMeters, MaxRadiusMeters).</summary>
    public double RadiusFactor { get; init; } = 1.62;

    public double MinRadiusMeters { get; init; } = 20;

    public double MaxRadiusMeters { get; init; } = 50;

    /// <summary>Заявку с меньшей грубой площадью телефон не отправляет: сервер всё равно откажет (его A_min — 2 500 м²).</summary>
    public double MinEstimatedAreaSquareMeters { get; init; } = 1_000;
}
