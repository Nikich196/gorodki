using System.Net;
using System.Text.Json;
using Gorodki.Api.Infrastructure.Persistence;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Готовность сервера на настоящей базе (PLAN.md, §7): база отвечает, бюджет хранения виден администратору; остальным —
/// только статусы.
/// </summary>
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
        var (admin, _) = await api.CreatePlayerClientAsync(UserRole.Admin);
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, client, Square(NewArea(), 0, 0, 100))).RunId);

        var anonymous = await api.CreateClient().GetAsync("/health/ready", Cancel);
        var response = await admin.GetAsync("/health/ready", Cancel);

        // Пингеру хватает кода и статусов: размер базы и число соединений посторонним не нужны.
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        using (var brief = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync(Cancel)))
        {
            Assert.Equal("healthy", brief.RootElement.GetProperty("status").GetString());
            Assert.Equal(["status"], brief.RootElement.GetProperty("checks").GetProperty("storage").EnumerateObject().Select(p => p.Name));
        }

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
        Assert.True(data.GetProperty("captureJournalParcelsBytes").GetInt64() > 0); // строки точного отката (BE-01) — своя строка бюджета
    }
}
