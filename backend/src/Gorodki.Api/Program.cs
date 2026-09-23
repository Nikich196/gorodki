using Gorodki.Api.Features.Health;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Time;
using Microsoft.EntityFrameworkCore;
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

// База данных подключается, только если задана строка подключения (на Render — переменная ConnectionStrings__Gorodki).
// Без неё сервер всё равно запускается: так проще разрабатывать и тестировать то, что базы не требует.
var connectionString = builder.Configuration.GetConnectionString("Gorodki");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<AppDbContext>(options => AppDbContext.Configure(options, connectionString));
    health.AddDbContextCheck<AppDbContext>("database");
}

// Время — только через TimeProvider и GameClock, чтобы тесты могли его подменить.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GameClock>();

var app = builder.Build();

// Миграции при запуске — только если явно включено (Database__MigrateOnStartup=true на Render).
if (!string.IsNullOrWhiteSpace(connectionString) && app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    // Описание API: /openapi/v1.json и интерактивная страница /scalar.
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapHealthEndpoints();

await app.RunAsync();
