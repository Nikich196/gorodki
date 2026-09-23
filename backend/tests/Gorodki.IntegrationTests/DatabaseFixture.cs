using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Одна база на все интеграционные тесты: образ <c>supabase/postgres:17.6.1.175</c> — тот же PostgreSQL 17 и PostGIS 3.3,
/// что на Supabase (у образа <c>postgis/postgis</c> нет сочетания 17 + 3.3). Суперпользователь образа — <c>supabase_admin</c>.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public const string Image = "supabase/postgres:17.6.1.175";

    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";

    /// <summary>Почему база недоступна (нет Docker) — тогда тесты пропускаются локально и падают в CI.</summary>
    public string? Unavailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder(Image)
                .WithUsername("supabase_admin")
                .WithPassword("only-for-tests")
                .WithDatabase("postgres")
                .Build();
            await _container.StartAsync();
        }
        catch (Exception e) when (Environment.GetEnvironmentVariable("CI") != "true")
        {
            Unavailable = $"Нет Docker: {e.GetType().Name}";
            return;
        }

        ConnectionString = AppDbContext.WithSearchPath(_container.GetConnectionString());
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(options, ConnectionString);
        return new AppDbContext(options.Options);
    }

    /// <summary>Пропустить тест, если Docker недоступен (только локально — в CI это ошибка).</summary>
    public void RequireDatabase()
    {
        if (Unavailable is not null)
        {
            Assert.Skip(Unavailable);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
