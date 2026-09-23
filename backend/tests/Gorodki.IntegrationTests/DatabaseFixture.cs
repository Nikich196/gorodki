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

        // Контейнер локальный: шифрование не нужно. Сервер в образе Supabase сбрасывает запрос SSL/GSS
        // (в CI соединение рвалось именно на согласовании шифрования). На настоящем Supabase SSL, конечно, включён.
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            SslMode = Npgsql.SslMode.Disable,
            GssEncryptionMode = Npgsql.GssEncryptionMode.Disable,
        };
        ConnectionString = AppDbContext.WithSearchPath(builder.ConnectionString);
        await WaitUntilStableAsync(ConnectionString, TimeSpan.FromMinutes(2));
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// Образ Supabase при первом запуске выполняет свои скрипты и перезапускает сервер: <c>pg_isready</c> может
    /// ответить «готово» до перезапуска (так и было в CI — первое подключение оборвалось). Поэтому ждём
    /// три успешных запроса подряд.
    /// </summary>
    private static async Task WaitUntilStableAsync(string connectionString, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var successes = 0;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var connection = new Npgsql.NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var command = new Npgsql.NpgsqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync();
                if (++successes >= 3)
                {
                    return;
                }
            }
            catch (Exception e) when (e is Npgsql.NpgsqlException or IOException or TimeoutException)
            {
                successes = 0;
                last = e;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException("База в контейнере так и не стала стабильно отвечать.", last);
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
