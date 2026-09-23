using System.Collections.Concurrent;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Config;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Config;

/// <summary>Версия конфига с моментом, с которого она действует.</summary>
public sealed record GameConfigVersion(int Version, DateTimeOffset ActiveFrom, GameConfig Rules);

/// <summary>Разобранные версии конфига. Версия после записи не меняется, поэтому её можно держать в памяти всегда.</summary>
public sealed class GameConfigCache
{
    private readonly ConcurrentDictionary<int, GameConfigVersion> _versions = new();

    public GameConfigVersion GetOrAdd(GameConfigEntity entity) =>
        _versions.GetOrAdd(entity.Version, _ => new GameConfigVersion(entity.Version, entity.ActiveFrom, GameConfig.FromJson(entity.Json)));

    public bool TryGet(int version, out GameConfigVersion config) => _versions.TryGetValue(version, out config!);
}

/// <summary>
/// Версии игрового конфига. Новая база получает версию 1 — числа по умолчанию из кода (<see cref="GameConfig.Default"/>),
/// дальше каждое изменение правил — новая версия.
/// </summary>
public sealed class GameConfigStore(AppDbContext db, GameConfigCache cache, TimeProvider time)
{
    /// <summary>Версия 1 действует «всегда»: забег с любой датой начала найдёт хотя бы её.</summary>
    public static readonly DateTimeOffset FirstVersionActiveFrom = DateTimeOffset.UnixEpoch;

    /// <summary>Действующая сейчас версия (самая новая из начавших действовать).</summary>
    public async Task<GameConfigVersion> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var current = await FindCurrentAsync(cancellationToken);
        if (current is not null)
        {
            return cache.GetOrAdd(current);
        }

        // Пустая таблица — новая база. ON CONFLICT: одновременные первые запросы не мешают друг другу.
        var json = GameConfig.Default.ToJson();
        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO app.game_configs (version, json, active_from, created_at)
            VALUES (1, {json}::jsonb, {FirstVersionActiveFrom}, {now})
            ON CONFLICT (version) DO NOTHING
            """,
            cancellationToken);

        current = await FindCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("Версия 1 игрового конфига не появилась после записи.");
        return cache.GetOrAdd(current);
    }

    /// <summary>Конкретная версия — ею проверяется забег, начатый с ней. <c>null</c>, если такой нет.</summary>
    public async Task<GameConfigVersion?> GetAsync(int version, CancellationToken cancellationToken)
    {
        if (cache.TryGet(version, out var cached))
        {
            return cached;
        }

        var entity = await db.GameConfigs.AsNoTracking().SingleOrDefaultAsync(c => c.Version == version, cancellationToken);
        return entity is null ? null : cache.GetOrAdd(entity);
    }

    /// <summary>
    /// Версия, с которой можно начать забег: она уже действует и действовала в момент старта — или её сменили недавно
    /// (телефон мог начать забег без сети со старым конфигом). Иначе <c>null</c>: взять «удобную» старую или ещё
    /// не вступившую версию нельзя.
    /// </summary>
    public async Task<GameConfigVersion?> GetForRunAsync(
        int version, DateTimeOffset startedAt, TimeSpan grace, CancellationToken cancellationToken)
    {
        await GetCurrentAsync(cancellationToken); // новая база получит версию 1
        var now = time.GetUtcNow();
        var config = await GetAsync(version, cancellationToken);
        if (config is null || config.ActiveFrom > now)
        {
            return null;
        }

        var replacedAt = await db.GameConfigs.AsNoTracking()
            .Where(c => c.Version > version && c.ActiveFrom <= now)
            .OrderBy(c => c.ActiveFrom)
            .Select(c => (DateTimeOffset?)c.ActiveFrom)
            .FirstOrDefaultAsync(cancellationToken);
        return replacedAt is null || replacedAt > startedAt || replacedAt >= now - grace ? config : null;
    }

    private Task<GameConfigEntity?> FindCurrentAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return db.GameConfigs.AsNoTracking()
            .Where(c => c.ActiveFrom <= now)
            .OrderByDescending(c => c.Version)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
