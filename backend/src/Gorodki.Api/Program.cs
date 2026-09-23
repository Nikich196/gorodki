using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Health;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Time;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
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
builder.Services.AddOpenApi();
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
    builder.Services.AddSingleton<GameConfigCache>();
    builder.Services.AddScoped<GameConfigStore>();

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
    app.MapConfigEndpoints();
}

await app.RunAsync();
