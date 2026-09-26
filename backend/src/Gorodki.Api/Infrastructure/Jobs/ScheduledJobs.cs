using Gorodki.Api.Features.Admin;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Time;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Npgsql;

namespace Gorodki.Api.Infrastructure.Jobs;

/// <summary>
/// Задачи по расписанию — Hangfire (ADR 0005, вариант В). Очередь захватов, визитов, тумана и откатов остаётся в
/// <c>CaptureWorker</c> (там нужен мгновенный сигнал и строго один поток), а сюда идут задачи «раз в час» и «в 00:00 по
/// Минску». Хранилище — схема <c>hangfire</c> в той же базе (Hangfire создаёт её сам, миграция EF не нужна), свой пул
/// на 3 соединения; дашборд <see cref="DashboardPath"/> — только чтение и только администратору.
/// </summary>
/// <remarks>
/// Новая задача — одна строка в <see cref="Register"/> по образцу чистки токенов: сервис из DI (scoped — своя область на
/// каждый запуск), метод, возвращающий <see cref="Task"/>, <c>CancellationToken.None</c> вместо токена (Hangfire подставит
/// свой — остановка сервера), расписание и часовой пояс Минска. Задача обязана быть идемпотентной: Hangfire повторяет
/// упавшую, а после сна Render Free догоняет пропущенный запуск один раз. Тест — вызвать метод напрямую дважды
/// (docs/architecture/jobs.md).
/// </remarks>
public static class ScheduledJobs
{
    /// <summary>
    /// Выключатель (по умолчанию включено). Без строки подключения Hangfire не подключается вовсе; тесты с сервером в памяти
    /// выключают его сами — иначе он при старте пошёл бы в базу, — кроме тех, что проверяют сам Hangfire.
    /// </summary>
    public const string EnabledSetting = "Jobs:Hangfire";

    /// <summary>Схема таблиц Hangfire в базе (PLAN.md, §7.3: схемы <c>app</c> и <c>hangfire</c>).</summary>
    public const string Schema = "hangfire";

    /// <summary>Потолок пула Hangfire (PLAN.md, §7.3: «приложение 8, Hangfire 3»): у бесплатной базы Supabase соединений мало.</summary>
    public const int MaxPoolSize = 3;

    /// <summary>Дашборд задач — для показа (§11): только чтение, только роль <c>admin</c>.</summary>
    public const string DashboardPath = "/admin/hangfire";

    /// <summary>
    /// Кука с access-токеном для дашборда: браузер не шлёт заголовок <c>Authorization</c>, поэтому токен приходит в адресе
    /// один раз (<c>?access_token=…</c>), а страница, её стили и опрос статистики дальше идут с этой кукой.
    /// </summary>
    public const string DashboardCookie = "gorodki_hangfire";

    /// <summary>Задача-образец: стереть истёкшие refresh-токены (раньше — часовая чистка <c>CaptureWorker</c>).</summary>
    public const string RefreshTokensJob = "refresh-tokens-purge";

    /// <summary>
    /// Смена сезона (PLAN.md, §3.4): каждый час в :00 по Минску — значит, и ровно в полночь начала сезона; в остальные часы
    /// задача только проверяет, что смена уже была. Сброс — один раз на сезон (<see cref="SeasonRollover"/>).
    /// </summary>
    public const string SeasonRolloverJob = "season-rollover";

    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue(EnabledSetting, defaultValue: true);

    public static IServiceCollection AddScheduledJobs(this IServiceCollection services, string connectionString)
    {
        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                options => options.UseNpgsqlConnection(StorageConnectionString(connectionString)),
                new PostgreSqlStorageOptions { SchemaName = Schema, PrepareSchemaIfNecessary = true }));

        // Задачи короткие и редкие: один исполнитель (по умолчанию — до 20), значит, и соединений он берёт меньше.
        services.AddHangfireServer(options => options.WorkerCount = 1);
        return services;
    }

    /// <summary>Дашборд и расписание. Вызывать после <c>UseAuthentication</c>/<c>UseAuthorization</c>: без входа дашборд — 401.</summary>
    public static WebApplication UseScheduledJobs(this WebApplication app)
    {
        // Токен из адреса — в куку, и сразу тот же адрес без токена: в ссылках страницы и её запросах его уже нет.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(DashboardPath)
                && context.Request.Query["access_token"].ToString() is { Length: > 0 } token
                && context.User.Identity?.IsAuthenticated == true)
            {
                context.Response.Cookies.Append(DashboardCookie, token, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    Path = DashboardPath,
                });
                context.Response.Redirect($"{context.Request.PathBase}{context.Request.Path}");
                return;
            }

            await next();
        });

        app.UseHangfireDashboard(DashboardPath, new DashboardOptions
        {
            Authorization = [], // по умолчанию — «только с этой машины»: за прокси Render это никто
            AsyncAuthorization = [new AdminOnly()],
            IsReadOnlyFunc = _ => true,
            DisplayStorageConnectionString = false, // иначе на странице — хост пула и имя базы
            DashboardTitle = "Городки — задачи по расписанию",
            AppPath = null,
            StatsPollingInterval = 10_000, // открытый дашборд опрашивает базу — не чаще раза в 10 с
        });

        Register(app.Services.GetRequiredService<IRecurringJobManager>());
        return app;
    }

    /// <summary>
    /// Расписание: все повторяющиеся задачи. Запись по имени — повторный запуск сервера и второй экземпляр во время
    /// деплоя задачу не дублируют, а обновляют.
    /// </summary>
    public static void Register(IRecurringJobManager jobs)
    {
        var minsk = new RecurringJobOptions { TimeZone = GameClock.MinskTimeZone };

        jobs.AddOrUpdate<RefreshTokenRetention>(RefreshTokensJob, r => r.PurgeExpiredAsync(CancellationToken.None), Cron.Hourly(), minsk);
        jobs.AddOrUpdate<SeasonRollover>(SeasonRolloverJob, r => r.RunIfDueAsync(CancellationToken.None), Cron.Hourly(), minsk);
    }

    /// <summary>Строка подключения Hangfire: та же база, свой пул на <see cref="MaxPoolSize"/> и своё имя в <c>pg_stat_activity</c>.</summary>
    public static string StorageConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(AppDbContext.WithSearchPath(connectionString))
        {
            MaxPoolSize = MaxPoolSize,
            ApplicationName = "gorodki-hangfire",
        }.ConnectionString;

    /// <summary>
    /// Дашборд — только администратору. Роль — по базе, как у админских адресов (<see cref="AdminEndpoints"/>): снятая роль
    /// действует сразу. Без входа запрос сюда не доходит (политика «по умолчанию — только с входом», 401), вошедшему не
    /// админу Hangfire отвечает 403.
    /// </summary>
    private sealed class AdminOnly : IDashboardAsyncAuthorizationFilter
    {
        public async Task<bool> AuthorizeAsync(DashboardContext context)
        {
            var http = context.GetHttpContext();
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            return await AdminEndpoints.AdminIdAsync(http.User, db, http.RequestAborted) is not null;
        }
    }
}
