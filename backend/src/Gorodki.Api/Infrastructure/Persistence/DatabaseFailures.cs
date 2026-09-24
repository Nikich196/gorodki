using Npgsql;

namespace Gorodki.Api.Infrastructure.Persistence;

/// <summary>Какие ошибки базы временные: работу с ними не записывают в неудачи, а повторяют позже.</summary>
public static class DatabaseFailures
{
    /// <summary>
    /// Временный сбой: соединение не открылось или оборвалось, пул занят, взаимоблокировка, база перезапускается — повтор
    /// той же работы позже может пройти. Решает Npgsql (<see cref="NpgsqlException.IsTransient"/>, тайм-аут — как в его
    /// стратегии выполнения), но EF Core такое исключение заворачивает: запросы, ExecuteUpdate и SaveChanges — в
    /// InvalidOperationException («likely due to a transient failure»), SaveChanges — ещё и в DbUpdateException; сырой SQL
    /// и начало транзакции — нет. Поэтому смотрим всю цепочку. Отмена по statement_timeout (57014) Npgsql временной не
    /// считает: тот же запрос на тех же данных упадёт так же.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException { IsTransient: true } or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Блокировка не далась за <c>lock_timeout</c> (55P03): её дольше держит другая работа — например, ежедневный срез
    /// рейтингов. Это «занято, повторите», а не ошибка сервера. Цепочку смотрим целиком, как в <see cref="IsTransient"/>:
    /// SaveChanges заворачивает ошибку в DbUpdateException. Отмена по statement_timeout (57014) сюда не входит: ожидание
    /// блокировки lock_timeout обрывает раньше, а запрос, долгий сам по себе, на тех же данных упадёт так же.
    /// </summary>
    public static bool IsLockTimeout(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable })
            {
                return true;
            }
        }

        return false;
    }
}
