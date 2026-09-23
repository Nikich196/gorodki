using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Config;
using Gorodki.Domain.Config;
using Microsoft.Extensions.DependencyInjection;

namespace Gorodki.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class ConfigTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_database_serves_version_1_with_the_default_numbers()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();

        var config = await client.GetFromJsonAsync<ConfigResponse>("/config", GameConfig.JsonOptions, Cancel);

        Assert.NotNull(config);
        Assert.Equal(1, config.Version);
        Assert.Equal(GameConfig.Default.ToJson(), config.Rules.ToJson());
    }

    [Fact]
    public async Task Config_needs_sign_in()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);

        var response = await api.CreateClient().GetAsync("/config", Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Run_is_checked_by_the_version_it_started_with()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        await using var scope = api.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<GameConfigStore>();

        var current = await store.GetCurrentAsync(Cancel);
        var sameVersion = await store.GetAsync(current.Version, Cancel);
        var unknown = await store.GetAsync(999_999, Cancel);

        Assert.NotNull(sameVersion);
        Assert.Equal(current.Rules.ToJson(), sameVersion.Rules.ToJson());
        Assert.Null(unknown);
    }
}
