using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Health;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace Gorodki.Api.Tests.Health;

/// <summary>
/// Проверяем сервер целиком, но в памяти: WebApplicationFactory запускает настоящий Program
/// без сети и без внешних сервисов.
/// </summary>
public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_reports_ok_and_game_day_in_Minsk()
    {
        // 21:30 UTC 15.11 — в Минске уже 00:30 16.11, первый день Сезона 0.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 21, 30, 0, TimeSpan.Zero));
        var client = factory
            .WithWebHostBuilder(host => host.ConfigureServices(services => services.AddSingleton<TimeProvider>(time)))
            .CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal("ok", body.Status);
        Assert.False(string.IsNullOrWhiteSpace(body.Version));
        Assert.Equal(new DateOnly(2026, 11, 16), body.GameDay);
        Assert.Equal(TimeSpan.FromHours(3), body.MinskTime.Offset);
    }

    [Fact]
    public async Task Readiness_probe_answers()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("healthy", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readiness_shows_why_the_database_failed_only_to_the_admin()
    {
        // База недоступна: в описании проверки — текст исключения (на Supabase это адрес пула или postgres.<ref проекта>).
        // Посторонним и игрокам — только статусы, администратору — подробности.
        await using var app = UnreachableDatabase.Server();
        var tokens = app.Services.GetRequiredService<TokenService>();
        var player = app.CreateClient();
        player.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.CreateAccessToken(User(UserRole.Player)));
        var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.CreateAccessToken(User(UserRole.Admin)));

        var detailed = await admin.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, detailed.StatusCode);
        using var report = JsonDocument.Parse(await detailed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var error = report.RootElement.GetProperty("checks").GetProperty("storage").GetProperty("description").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));

        foreach (var client in new[] { app.CreateClient(), player })
        {
            var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(error!, text);
            using var body = JsonDocument.Parse(text);
            Assert.Equal("unhealthy", body.RootElement.GetProperty("status").GetString());
            var storage = body.RootElement.GetProperty("checks").GetProperty("storage");
            Assert.Equal("unhealthy", storage.GetProperty("status").GetString());
            Assert.Equal(["status"], storage.EnumerateObject().Select(p => p.Name)); // ни описания, ни чисел
        }
    }

    [Theory]
    [InlineData(349, HealthStatus.Healthy)]
    [InlineData(350, HealthStatus.Degraded)]
    [InlineData(499, HealthStatus.Degraded)]
    public void Storage_warns_from_350_megabytes(long megabytes, HealthStatus expected)
    {
        Assert.Equal(expected, StorageHealthCheck.Evaluate(megabytes * 1024 * 1024));
    }

    [Fact]
    public async Task Unknown_address_returns_problem_details()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/no-such-endpoint", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static UserEntity User(UserRole role) => new()
    {
        Id = Guid.CreateVersion7(),
        DisplayName = "Тест",
        NormalizedName = "тест",
        Role = role,
    };
}
