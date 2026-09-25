using System.Net;
using System.Text.RegularExpressions;
using Gorodki.Api.Features.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Gorodki.Api.Tests.Architecture;

/// <summary>
/// Что открыто без входа (docs/architecture/auth.md: «всё закрыто по умолчанию: открытые адреса помечаются явно»). Адреса
/// берутся у самого сервера (<see cref="EndpointDataSource"/>): новый открытый адрес, не внесённый в список, роняет тест —
/// как и потерянная политика «по умолчанию — только с входом».
/// </summary>
public sealed partial class AnonymousAccessTests
{
    /// <summary>Открыты на сервере: проверки для Render и пингера и сам вход.</summary>
    private static readonly string[] Open =
    [
        "* /health/ready",
        "GET /health",
        "POST /auth/google",
        "POST /auth/logout",
        "POST /auth/refresh",
    ];

    /// <summary>Только в разработке: описание API и его страница (Scalar со своими файлами), данных игроков в них нет.</summary>
    private static readonly string[] OpenInDevelopment =
    [
        "GET /openapi/{documentName}.json",
        "GET /scalar/favicon.svg",
        "GET /scalar/scalar.aspnetcore.js",
        "GET /scalar/scalar.js",
        "GET /scalar/{documentName?}",
    ];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Only_approved_endpoints_are_open_without_sign_in(string environment)
    {
        await using var app = Server(environment);
        var policies = app.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var open = new List<string>();
        foreach (var endpoint in Endpoints(app))
        {
            if (await IsOpenAsync(endpoint, policies))
            {
                open.AddRange(Names(endpoint));
            }
        }

        string[] expected = environment == "Development" ? [.. Open, .. OpenInDevelopment] : Open;
        Assert.Equal(expected.Order(StringComparer.Ordinal), open.Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Every_other_endpoint_answers_an_anonymous_caller_with_401()
    {
        // То же — запросами через весь конвейер: адрес закрыт на деле, а не только в метаданных.
        await using var app = Server("Production");
        var client = app.CreateClient();
        var closed = Endpoints(app).SelectMany(e => Names(e).Select(name => (Name: name, Endpoint: e)))
            .Where(e => !Open.Contains(e.Name))
            .ToList();
        Assert.Contains(closed, e => e.Name == "GET /me"); // список не пуст по ошибке
        Assert.Contains(closed, e => e.Name == "DELETE /fog/");

        foreach (var (name, endpoint) in closed)
        {
            var method = name[..name.IndexOf(' ', StringComparison.Ordinal)];
            using var request = new HttpRequestMessage(new HttpMethod(method == "*" ? "GET" : method), SamplePath(endpoint));
            using var response = await client.SendAsync(request, Cancel);

            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{name}: {(int)response.StatusCode} без входа");
        }
    }

    [Fact]
    public async Task Realtime_hub_requires_sign_in_on_its_own()
    {
        // Хаб закрыт своим [Authorize], а не только политикой по умолчанию: подсказки — только вошедшим (realtime.md).
        await using var app = Server("Production");
        var hub = Endpoints(app).Where(e => e.RoutePattern.RawText!.StartsWith(GameHub.Path, StringComparison.Ordinal)).ToList();

        Assert.Contains(hub, e => e.RoutePattern.RawText == $"{GameHub.Path}/negotiate");
        Assert.All(hub, e =>
        {
            Assert.NotEmpty(e.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Null(e.Metadata.GetMetadata<IAllowAnonymous>());
        });
        var negotiate = await app.CreateClient().PostAsync($"{GameHub.Path}/negotiate?negotiateVersion=1", null, Cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    private static WebApplicationFactory<Program> Server(string environment) =>
        UnreachableDatabase.Server().WithWebHostBuilder(builder => builder.UseEnvironment(environment));

    private static List<RouteEndpoint> Endpoints(WebApplicationFactory<Program> app)
    {
        _ = app.CreateClient(); // сервер собирается при первом клиенте
        return [.. app.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()];
    }

    private static IEnumerable<string> Names(RouteEndpoint endpoint) =>
        (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
            .Select(method => $"{method} {endpoint.RoutePattern.RawText}");

    /// <summary>
    /// Как решает <c>AuthorizationMiddleware</c>: <c>AllowAnonymous</c> открывает адрес, иначе действуют его политики, а без
    /// них — политика по умолчанию (fallback). Нет ни одной — адрес открыт.
    /// </summary>
    private static async Task<bool> IsOpenAsync(Endpoint endpoint, IAuthorizationPolicyProvider policies)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return true;
        }

        var policy = await AuthorizationPolicy.CombineAsync(
            policies, endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
        policy ??= await policies.GetFallbackPolicyAsync();
        return policy is null;
    }

    /// <summary>Адрес с подставленными параметрами: номер — новый Guid, число — 1.</summary>
    private static string SamplePath(RouteEndpoint endpoint) =>
        Parameter().Replace(endpoint.RoutePattern.RawText!, match => match.Groups["constraint"].Value switch
        {
            "guid" => Guid.NewGuid().ToString(),
            "int" => "1",
            _ => "x",
        });

    [GeneratedRegex(@"\{(?<name>[^}:?]+)(:(?<constraint>[^}?]+))?\??\}")]
    private static partial Regex Parameter();
}
