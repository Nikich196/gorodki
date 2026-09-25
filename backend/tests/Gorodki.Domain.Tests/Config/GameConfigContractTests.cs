using System.Text.Json.Nodes;
using Gorodki.Domain.Config;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Territory;

namespace Gorodki.Domain.Tests.Config;

/// <summary>
/// Конфиг версии 1 — контракт между сервером и телефоном: <c>contracts/game-config.v1.json</c>. Этот тест держит
/// сервер, <c>ContractTests</c> в GameCore — телефон. Обновить файл после намеренной правки чисел:
/// <c>GORODKI_UPDATE_CONTRACTS=1 dotnet test</c> (и объяснить правку в PR).
/// </summary>
public sealed class GameConfigContractTests
{
    private static readonly string ContractPath = Path.Combine(RepositoryRoot(), "contracts", "game-config.v1.json");

    [Fact]
    public void Default_config_matches_the_contract_file()
    {
        var actual = GameConfig.Default.ToJson();
        if (Environment.GetEnvironmentVariable("GORODKI_UPDATE_CONTRACTS") == "1")
        {
            File.WriteAllText(ContractPath, actual.ReplaceLineEndings("\n") + "\n");
        }

        var expected = File.ReadAllText(ContractPath);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            $"Числа конфига в коде разошлись с {ContractPath}. Если правка намеренная — обновите файл (см. комментарий к тесту).");
    }

    [Fact]
    public void Contract_file_reads_back_into_the_same_config()
    {
        var restored = GameConfig.FromJson(File.ReadAllText(ContractPath));

        Assert.Equal(GameConfig.Default.ToJson(), restored.ToJson());
    }

    [Fact]
    public void Numbers_follow_the_plan()
    {
        var config = GameConfig.Default;

        // §3.2 — захват.
        Assert.Equal(150, config.Capture.LoopDetector.MinPathMeters);
        Assert.Equal((20.0, 50.0), (config.Capture.LoopDetector.MinRadiusMeters, config.Capture.LoopDetector.MaxRadiusMeters));
        Assert.Equal(1.62, config.Capture.LoopDetector.RadiusFactor);
        Assert.Equal(2_500, config.Capture.Shape.MinAreaSquareMeters);
        Assert.Equal(9, config.Capture.Shape.MinHalfWidthMeters);
        Assert.Equal(3_500_000, config.Capture.Shape.MaxAreaSquareMeters);
        Assert.Equal(2, config.Capture.Shape.SimplifyToleranceMeters);
        Assert.Equal(30, config.Capture.MaxCapturesPerDay);
        Assert.Equal(4, config.Capture.MaxRunHours);

        // §3.9 — античит, слой 1.
        Assert.Equal(25, config.Leagues.Run.MaxAccuracyMeters);
        Assert.Equal(35, config.Capture.NewcomerMaxAccuracyMeters);
        Assert.Equal(10, config.Leagues.Run.MaxFixAgeSeconds);

        // §3.9 — слой 2: бег и вело.
        Assert.Equal(new[] { new SpeedLimit(30, 25), new SpeedLimit(300, 19) }, config.Leagues.Run.SpeedLimits);
        Assert.Equal(new[] { 0.3, 2.2 }, config.Leagues.Run.StrideMeters!);
        Assert.Equal((20.0, 30.0), (config.Leagues.Run.VehicleSeconds!.Value, config.Leagues.Run.CyclingSeconds!.Value));
        Assert.Equal(
            new[] { new SpeedLimit(5, 60), new SpeedLimit(30, 48), new SpeedLimit(60, 42), new SpeedLimit(300, 36) },
            config.Leagues.Bike.SpeedLimits);
        Assert.Equal(new CarLaunchRule(25, 6, 35), config.Leagues.Bike.CarLaunch);
        Assert.Equal(new VehicleShareRule(120, 0.6, 60, 25), config.Leagues.Bike.VehicleShare);

        // §3.3 и §3.10.
        Assert.Equal(3, config.Territory.MaxLevel);
        Assert.Equal(20, config.Territory.LevelUpIntervalHours);
        Assert.Equal((20.0, 2), (config.Territory.LevelLossWindowHours, config.Territory.MaxLevelsLostPerWindow));
        Assert.Equal((6.0, 3.0), (config.Territory.DecayDaysPerLevel, config.Territory.GhostDays));
        Assert.Equal((500_000.0, 2_000_000.0), (config.Territory.BigLoopSquareMeters.Run, config.Territory.BigLoopSquareMeters.Bike));
        Assert.Equal(24, config.Territory.ContestedHours);
        Assert.Equal(25, config.Exploration.RevealRadiusMeters);
        Assert.Equal(2, config.Exploration.RadarMultiplier);

        // §7.2 — лимиты разрывов тумана.
        Assert.Equal((100.0, 200.0), (config.Exploration.MaxGapMeters.For(League.Run), config.Exploration.MaxGapMeters.For(League.Bike)));
    }

    [Fact]
    public void Territory_numbers_in_config_are_the_engine_defaults()
    {
        Assert.Equal(new TerritoryRules(), new TerritoryConfig().ToRules());
    }

    [Fact]
    public void Big_loop_is_strictly_above_the_league_threshold()
    {
        // §3.3: «если P > 0,5 км² (в вело-лиге — 2 км²)».
        var territory = GameConfig.Default.Territory;

        Assert.False(territory.IsBigLoop(League.Run, 500_000));
        Assert.True(territory.IsBigLoop(League.Run, 500_000.1));
        Assert.False(territory.IsBigLoop(League.Bike, 1_999_999));
        Assert.True(territory.IsBigLoop(League.Bike, 2_000_000.1));
    }

    [Fact]
    public void Each_league_gets_its_own_rules()
    {
        var leagues = GameConfig.Default.Leagues;

        Assert.Same(leagues.Run, leagues.For(League.Run));
        Assert.Same(leagues.Bike, leagues.For(League.Bike));
    }

    /// <summary>Корень репозитория: папка, где рядом лежат <c>backend</c> и <c>docs</c>.</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "backend")) && Directory.Exists(Path.Combine(directory.FullName, "docs")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Не найден корень репозитория (папки backend и docs).");
    }
}
