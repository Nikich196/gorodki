using Gorodki.Api.Features.Health;
using Gorodki.Domain.Time;
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
builder.Services.AddHealthChecks();

// Время — только через TimeProvider и GameClock, чтобы тесты могли его подменить.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GameClock>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    // Описание API: /openapi/v1.json и интерактивная страница /scalar.
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapHealthEndpoints();

app.Run();
