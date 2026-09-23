using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Health;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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
    }

    [Fact]
    public async Task Unknown_address_returns_problem_details()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/no-such-endpoint", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
