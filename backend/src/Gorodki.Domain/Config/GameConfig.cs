using System.Text.Json;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Territory;

namespace Gorodki.Domain.Config;

/// <summary>
/// Все числа правил игры (PLAN.md, §3). В базе хранятся версиями (<c>game_configs</c>): телефон берёт конфиг при каждом
/// «Старте», забег проверяется той версией, с которой начат. Здесь — значения версии 1; их JSON лежит в
/// <c>contracts/game-config.v1.json</c> и проверяется тестами сервера и телефона.
/// </summary>
public sealed record GameConfig
{
    public CaptureConfig Capture { get; init; } = new();

    public TerritoryConfig Territory { get; init; } = new();

    public LeaguesConfig Leagues { get; init; } = new();

    public ExplorationConfig Exploration { get; init; } = new();

    public static GameConfig Default { get; } = new();

    /// <summary>Как конфиг хранится в базе и в контракте: имена в camelCase, как в API.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static GameConfig FromJson(string json) =>
        JsonSerializer.Deserialize<GameConfig>(json, JsonOptions) ?? throw new JsonException("Пустой игровой конфиг.");
}

/// <summary>Захват (PLAN.md, §3.2).</summary>
public sealed record CaptureConfig
{
    public LoopDetectorSettings LoopDetector { get; init; } = new();

    /// <summary>Построение контура на сервере: упрощение, A_min, R_min, потолок площади.</summary>
    public CaptureShapeSettings Shape { get; init; } = new();

    public int MaxCapturesPerDay { get; init; } = 30;

    /// <summary>Суточный потолок площади захватов, м². План его предусматривает, но число ещё не выбрано: <c>null</c> — без потолка.</summary>
    public double? MaxDailyAreaSquareMeters { get; init; }

    /// <summary>Забег длиннее — закрывается.</summary>
    public double MaxRunHours { get; init; } = 4;

    /// <summary>Порог точности для первой петли новичка, метры (обычный — в правилах лиги).</summary>
    public double NewcomerMaxAccuracyMeters { get; init; } = 35;
}

/// <summary>Участки (PLAN.md, §3.3). Интервалы — числами в часах: так их проще читать телефону.</summary>
public sealed record TerritoryConfig
{
    public int MaxLevel { get; init; } = 3;

    public double LevelUpIntervalHours { get; init; } = 20;

    public double TransferShieldHours { get; init; } = 12;

    public double SiegeHours { get; init; } = 24;

    public TerritoryRules ToRules() => new()
    {
        MaxLevel = MaxLevel,
        LevelUpInterval = TimeSpan.FromHours(LevelUpIntervalHours),
        TransferShield = TimeSpan.FromHours(TransferShieldHours),
        Siege = TimeSpan.FromHours(SiegeHours),
    };
}

/// <summary>Правила двух лиг (PLAN.md, D16 и §3.9).</summary>
public sealed record LeaguesConfig
{
    public LeagueRules Run { get; init; } = LeagueRules.Run;

    public LeagueRules Bike { get; init; } = LeagueRules.Bike;

    public LeagueRules For(League league) => league switch
    {
        League.Run => Run,
        League.Bike => Bike,
        _ => throw new ArgumentOutOfRangeException(nameof(league), league, "Неизвестная лига."),
    };
}

/// <summary>«Исследование» — туман (PLAN.md, §3.10).</summary>
public sealed record ExplorationConfig
{
    /// <summary>Валидное движение открывает карту в этом радиусе вокруг пути, метры.</summary>
    public double RevealRadiusMeters { get; init; } = 25;

    /// <summary>
    /// Между точками дальше этого путь не «закрашивается»: GPS пропадал, и где шёл игрок — неизвестно
    /// (PLAN.md, §7.2: 100 м пешком, 200 м на велосипеде).
    /// </summary>
    public PerLeague<double> MaxGapMeters { get; init; } = new(Run: 100, Bike: 200);

    /// <summary>Во сколько раз «Радар» увеличивает радиус.</summary>
    public double RadarMultiplier { get; init; } = 2;
}

/// <summary>Значение, своё для каждой лиги.</summary>
public sealed record PerLeague<T>(T Run, T Bike)
{
    public T For(League league) => league switch
    {
        League.Run => Run,
        League.Bike => Bike,
        _ => throw new ArgumentOutOfRangeException(nameof(league), league, "Неизвестная лига."),
    };
}
