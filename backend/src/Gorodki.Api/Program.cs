using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Gorodki.Api.Features.Admin;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Health;
using Gorodki.Api.Features.Leaderboards;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Features.Realtime;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Features.Seasons;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.OpenApi;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Time;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Render сообщает порт, который должен слушать сервис, через переменную окружения PORT.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
{
    builder.WebHost.UseUrls($"http://+:{port}");
}

// Ошибки отдаём в едином формате ProblemDetails (RFC 9457).
builder.Services.AddProblemDetails();

// Перечисления в JSON — строками (`"run"`, `"finished"`), как в GameCore; числа не принимаются.
// Числа — только числами: иначе описание API объявляет каждое число «целым или строкой», и клиент для iOS
// получает неудобный тип-вариант (найдено в спайке S6).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddOpenApi(options => options.AddDocumentTransformer(SwiftFriendlySchemas.Apply));
var health = builder.Services.AddHealthChecks();

// Время — только через TimeProvider и GameClock, чтобы тесты могли его подменить.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GameClock>();

// База данных и всё, что без неё не работает (вход, профиль), подключаются, только если задана строка подключения
// (на Render — переменная ConnectionStrings__Gorodki). Без неё сервер всё равно запускается: так проще разрабатывать
// и тестировать то, что базы не требует.
var connectionString = builder.Configuration.GetConnectionString("Gorodki");
var withDatabase = !string.IsNullOrWhiteSpace(connectionString);
if (withDatabase)
{
    builder.Services.AddDbContext<AppDbContext>(options => AppDbContext.Configure(options, connectionString!));
    health.AddDbContextCheck<AppDbContext>("database");
    health.AddCheck<StorageHealthCheck>("storage");
    builder.Services.AddSingleton<GameConfigCache>();
    builder.Services.AddScoped<GameConfigStore>();

    // Обработка захватов: заявки петель → проверка → земля. Фоновый обработчик можно выключить (так делают тесты).
    builder.Services.AddSingleton<CaptureSignal>();

    // Реальное время (PLAN.md, D6): подсказки «тайлы изменились» и «заявка решена» через SignalR. Хаб в базу не ходит.
    builder.Services.AddSignalR(options =>
    {
        options.MaximumReceiveMessageSize = 1024; // клиент шлёт только «подпишись на лигу»
        options.EnableDetailedErrors = false;
    });
    builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
    builder.Services.AddSingleton<HubConnections>();
    builder.Services.AddSingleton<RealtimeHints>();
    builder.Services.AddSingleton<RevealScanner>();
    builder.Services.AddSingleton<RealtimePump>();
    if (builder.Configuration.GetValue(RealtimePump.EnabledSetting, defaultValue: true))
    {
        builder.Services.AddHostedService(services => services.GetRequiredService<RealtimePump>());
    }
    builder.Services.AddScoped<RunJudgements>();
    builder.Services.AddScoped<RunRetention>();
    builder.Services.AddScoped<AccountDeletion>();
    builder.Services.AddScoped<LeaderboardSnapshots>();
    builder.Services.AddScoped<AccountExport>();
    builder.Services.AddScoped<CaptureProcessor>();
    builder.Services.AddScoped<CaptureRollback>();
    builder.Services.AddScoped<VisitProcessor>();
    builder.Services.AddScoped<SeasonStore>();
    builder.Services.AddScoped<FogProcessor>();
    builder.Services.AddScoped<TerritoryReader>();
    if (builder.Configuration.GetValue(CaptureWorker.EnabledSetting, defaultValue: true))
    {
        builder.Services.AddHostedService<CaptureWorker>();
    }

    var authSection = builder.Configuration.GetSection(AuthOptions.Section);
    var auth = authSection.Get<AuthOptions>() ?? new AuthOptions();
    var signingKey = TokenService.SigningKey(auth); // без ключа сервер не стартует — и сразу говорит почему

    builder.Services.AddOptions<AuthOptions>().Bind(authSection);
    builder.Services.AddSingleton<TokenService>();
    builder.Services.AddSingleton<IGoogleTokenValidator, GoogleTokenValidator>();
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;

            // WebSocket из браузера и часть клиентов не умеют заголовок Authorization — токен приходит в строке запроса.
            // Принимаем его так только на адресе хаба: в остальных адресах токену в URL (и в логах) не место.
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var token = context.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments(GameHub.Path))
                    {
                        context.Token = token;
                    }

                    return Task.CompletedTask;
                },
            };
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = auth.Issuer,
                ValidAudience = auth.Audience,
                IssuerSigningKey = signingKey,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = JwtRegisteredClaimNames.Sub,
                RoleClaimType = TokenService.RoleClaim,
            };
        });

    // Приём забегов: не больше 60 запросов в минуту на игрока (с запасом 120 — для выгрузки накопленного офлайн).
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(RunLimits.RateLimitPolicy, context => RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 120,
                TokensPerPeriod = 60,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        // Чтение (опрос заявок) — отдельный, более щедрый лимит: не должен съедать лимит выгрузки кусков после офлайна.
        options.AddPolicy(CaptureEndpoints.ReadRateLimitPolicy, context => RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 240,
                TokensPerPeriod = 120,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    });

    // Всё закрыто по умолчанию: открытые адреса помечаются явно (AllowAnonymous).
    builder.Services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
}

var app = builder.Build();

// Миграции при запуске — только если явно включено (Database__MigrateOnStartup=true на Render).
if (withDatabase && app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (withDatabase)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();
}

if (app.Environment.IsDevelopment())
{
    // Описание API: /openapi/v1.json и интерактивная страница /scalar.
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

app.MapHealthEndpoints();

if (withDatabase)
{
    app.MapAuthEndpoints();
    app.MapMeEndpoints();
    app.MapPrivacyZoneEndpoints();
    app.MapHub<GameHub>(GameHub.Path, options =>
    {
        // Истёк access-токен (15 минут) — соединение закрывается: удалённый или замороженный игрок не держит сокет вечно.
        options.CloseOnAuthenticationExpiration = true;
        options.TransportMaxBufferSize = 32 * 1024;
        options.ApplicationMaxBufferSize = 32 * 1024;
    });
    app.MapConfigEndpoints();
    app.MapRunEndpoints();
    app.MapCaptureEndpoints();
    app.MapAdminEndpoints();
    app.MapSeasonEndpoints();
    app.MapTerritoryEndpoints();
    app.MapFogEndpoints();
    app.MapLeaderboardEndpoints();
}

await app.RunAsync();
