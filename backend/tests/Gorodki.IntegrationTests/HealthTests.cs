using System.Net;
using System.Text.Json;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>Готовность сервера на настоящей базе (PLAN.md, §7): база отвечает, бюджет хранения виден в ответе.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class HealthTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Readiness_reports_the_storage_budget()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, client, Square(NewArea(), 0, 0, 100))).RunId);

        var response = await api.CreateClient().GetAsync("/health/ready", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));
        Assert.Equal("healthy", body.RootElement.GetProperty("status").GetString());
        var checks = body.RootElement.GetProperty("checks");
        Assert.Equal("healthy", checks.GetProperty("database").GetProperty("status").GetString());
        var storage = checks.GetProperty("storage");
        Assert.Equal("healthy", storage.GetProperty("status").GetString());
        var data = storage.GetProperty("data");
        Assert.True(data.GetProperty("databaseBytes").GetInt64() > 0);
        Assert.Equal(350, data.GetProperty("warnMegabytes").GetInt64());
        Assert.True(data.GetProperty("connections").GetInt64() >= 1);
        var parcels = data.GetProperty("parcels").GetInt64();
        Assert.True(parcels >= 1); // квадрат из этого теста
        Assert.True(data.GetProperty("vertices").GetInt64() >= parcels * 4);
    }
}
