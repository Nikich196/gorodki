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
            // Testcontainers заменяет команду запуска образа своей («-c fsync=off …»), и теряется «-D /etc/postgresql»
            // из образа Supabase. Без него сервер берёт настройки по умолчанию и слушает только localhost внутри
            // контейнера: pg_isready изнутри проходит, а подключения снаружи Docker сбрасывает. Поэтому явно
            // подключаем конфиг Supabase — так же запускает этот образ Supabase CLI.
            _container = new PostgreSqlBuilder(Image)
                .WithUsername("supabase_admin")
                .WithPassword("only-for-tests")
                .WithDatabase("postgres")
                .WithCommand("-c", "config_file=/etc/postgresql/postgresql.conf")
                .Build();
            await _container.StartAsync();
        }
        catch (Exception e) when (Environment.GetEnvironmentVariable("CI") != "true")
        {
            Unavailable = $"Нет Docker: {e.GetType().Name}";
            return;
        }

        // В конфиге образа ssl = off, а контейнер локальный: шифрование не согласуем. На настоящем Supabase SSL включён.
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            SslMode = Npgsql.SslMode.Disable,
            GssEncryptionMode = Npgsql.GssEncryptionMode.Disable,
        };
        ConnectionString = AppDbContext.WithSearchPath(builder.ConnectionString);
        try
        {
            await WaitUntilStableAsync(ConnectionString, TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException e)
        {
            // Без журнала контейнера причину не понять — прикладываем его конец к ошибке.
            var (stdout, stderr) = await _container.GetLogsAsync();
            throw new TimeoutException($"{e.Message}\n--- журнал контейнера ---\n{Tail(stdout)}\n{Tail(stderr)}", e);
        }

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// Образ Supabase при первом запуске выполняет свои скрипты на временном сервере и перезапускает его, а
    /// <c>pg_isready</c> проверяет изнутри контейнера. Поэтому готовность проверяем снаружи, как настоящий клиент:
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

    private static string Tail(string log, int lines = 40) =>
        string.Join('\n', log.Split('\n').TakeLast(lines));

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
