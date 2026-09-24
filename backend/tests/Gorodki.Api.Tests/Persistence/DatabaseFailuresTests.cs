using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gorodki.Api.Tests.Persistence;

/// <summary>
/// Временные сбои базы: фоновый обработчик не записывает их в неудачи (откат захвата), а повторяет работу в следующем проходе.
/// </summary>
public sealed class DatabaseFailuresTests
{
    [Fact]
    public async Task Unreachable_database_is_transient_however_EF_Core_wraps_it()
    {
        // Настоящие исключения EF Core, а не их подделка: запрос и SaveChanges стратегия выполнения Npgsql заворачивает
        // в InvalidOperationException, сырой SQL и начало транзакции отдают NpgsqlException как есть.
        var options = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(options, UnreachableDatabase.ConnectionString);
        await using var db = new AppDbContext(options.Options);
        var cancel = TestContext.Current.CancellationToken;

        var query = await Assert.ThrowsAnyAsync<Exception>(() => db.CaptureRollbacks.AsNoTracking().ToListAsync(cancel));
        var sql = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("SELECT 1", cancel));
        var transaction = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.BeginTransactionAsync(cancel));
        db.CaptureRollbacks.Add(new CaptureRollbackEntity { Id = Guid.CreateVersion7(), Reason = "тест" });
        var save = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(cancel));

        Assert.IsType<InvalidOperationException>(query); // обёртка — сама по себе не NpgsqlException
        Assert.IsType<InvalidOperationException>(save);
        Assert.True(DatabaseFailures.IsTransient(query), query.ToString());
        Assert.True(DatabaseFailures.IsTransient(sql), sql.ToString());
        Assert.True(DatabaseFailures.IsTransient(transaction), transaction.ToString());
        Assert.True(DatabaseFailures.IsTransient(save), save.ToString());
    }

    [Fact]
    public void Deadlock_shutdown_and_busy_pool_are_transient_at_any_depth()
    {
        Assert.True(DatabaseFailures.IsTransient(new DbUpdateException("save", Postgres(PostgresErrorCodes.DeadlockDetected))));
        Assert.True(DatabaseFailures.IsTransient(
            new InvalidOperationException("strategy", new DbUpdateException("save", Postgres(PostgresErrorCodes.SerializationFailure)))));
        Assert.True(DatabaseFailures.IsTransient(Postgres(PostgresErrorCodes.AdminShutdown)));
        Assert.True(DatabaseFailures.IsTransient(new NpgsqlException("The connection pool has been exhausted", new TimeoutException())));
        Assert.True(DatabaseFailures.IsTransient(new TimeoutException())); // как в стратегии выполнения Npgsql
    }

    [Fact]
    public void Errors_that_would_repeat_on_retry_are_not_transient()
    {
        // Повтор на тех же данных упадёт так же: это неудача работы, а не повод ждать следующего прохода.
        Assert.False(DatabaseFailures.IsTransient(Postgres(PostgresErrorCodes.CheckViolation)));
        Assert.False(DatabaseFailures.IsTransient(new DbUpdateException("save", Postgres(PostgresErrorCodes.CheckViolation))));
        Assert.False(DatabaseFailures.IsTransient(Postgres(PostgresErrorCodes.QueryCanceled))); // statement_timeout
        Assert.False(DatabaseFailures.IsTransient(new FormatException("Запись TWKB обрезана.")));
        Assert.False(DatabaseFailures.IsTransient(new InvalidOperationException("Sequence contains no elements")));
    }

    [Fact]
    public void Lock_timeout_is_recognised_at_any_depth_and_statement_timeout_is_not()
    {
        Assert.True(DatabaseFailures.IsLockTimeout(Postgres(PostgresErrorCodes.LockNotAvailable)));
        Assert.True(DatabaseFailures.IsLockTimeout(new DbUpdateException("save", Postgres(PostgresErrorCodes.LockNotAvailable))));
        Assert.False(DatabaseFailures.IsLockTimeout(Postgres(PostgresErrorCodes.QueryCanceled))); // statement_timeout
        Assert.False(DatabaseFailures.IsLockTimeout(Postgres(PostgresErrorCodes.DeadlockDetected)));
        Assert.False(DatabaseFailures.IsLockTimeout(new InvalidOperationException("Sequence contains no elements")));
    }

    private static PostgresException Postgres(string sqlState) => new("сбой", "ERROR", "ERROR", sqlState);
}
