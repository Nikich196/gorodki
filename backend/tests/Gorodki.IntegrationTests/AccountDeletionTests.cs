using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Удаление аккаунта на настоящей базе (PLAN.md, §3.16, закон 99-З; §11: «полнота удаления»): после стирания номера
/// игрока нет ни в одной таблице, кроме журнала чужих захватов (он стирается через 7 дней), его земля — ничья.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AccountDeletionTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deleted_account_leaves_nothing_behind_and_its_land_becomes_neutral()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);

        // У Анны есть всё: земля, забег с точками, туман, токен обновления.
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        var walk = await WalkAndFinishAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        await using (var scope = api.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<FogProcessor>().StampRunAsync(walk.Id, Cancel) > 0);
            await using var db = database.CreateContext();
            db.RefreshTokens.Add(scope.ServiceProvider.GetRequiredService<TokenService>().CreateRefreshToken(annaId, Guid.CreateVersion7()).Entity);
            await db.SaveChangesAsync(Cancel);
        }

        // Запрос: принят, повтор не сдвигает срок, вход в удаляемый аккаунт закрыт.
        var first = await anna.DeleteAsync("/me", Cancel);
        var again = await anna.DeleteAsync("/me", Cancel);
        var signIn = await api.CreateClient().PostAsJsonAsync("/auth/google", new GoogleSignInRequest($"google:{annaId:N}", null, true, 1), Cancel);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var requested = (await first.Content.ReadFromJsonAsync<AccountDeletionResponse>(Json, Cancel))!;
        Assert.Equal(requested, await again.Content.ReadFromJsonAsync<AccountDeletionResponse>(Json, Cancel));
        Assert.Equal(TimeSpan.FromDays(15).TotalMilliseconds, requested.DeleteByMs - requested.RequestedAtMs);
        Assert.Equal(HttpStatusCode.Forbidden, signIn.StatusCode);
        Assert.Contains("account_deleting", await signIn.Content.ReadAsStringAsync(Cancel));

        // Раньше 20 минут после запроса аккаунт не стирается: стёртая земля проступила бы контуром недавней петли.
        Assert.Equal(0, await DeleteRequestedAsync(api));
        Assert.True(await LandAreaAsync(annaId) > 0);

        // Через 25 минут Борис отнимает у Анны половину квадрата (его захват ещё скрыт), и аккаунт стирается.
        api.Time.Advance(TimeSpan.FromMinutes(25));
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100))).RunId);
        // Анна успела снять уровень у земли Бориса: её номер — в списке атакующих его участка и в журнале.
        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE app.parcels SET loss_attackers = ARRAY[{annaId}] WHERE owner_id = {borisId}", Cancel);
            await db.Database.ExecuteSqlAsync($"UPDATE app.capture_journal_pieces SET loss_attackers = ARRAY[{annaId}] WHERE owner_id = {borisId}", Cancel);
        }

        var versionBefore = await TileVersionAsync(tile);
        Assert.True(await DeleteRequestedAsync(api) >= 1);

        // Полнота: номер Анны остался только в журнале чужого захвата — «земля до» захвата Бориса.
        Assert.Equal(["capture_journal_pieces.owner_id"], await TablesMentioningAsync(annaId));
        Assert.InRange(await LandAreaAsync(borisId), 9_700, 10_300); // земля Бориса не тронута
        Assert.True(await TileVersionAsync(tile) > versionBefore); // у соседей карта обновится

        // Захват Бориса ещё скрыт задержкой (20 минут): проекция вернула бы Вере землю Анны — но удалённый на карте
        // не появляется.
        var seen = await vera.GetFromJsonAsync<TerritoryResponse>($"/territory?league=run&tiles={tile.X}:{tile.Y}", Json, Cancel);
        Assert.DoesNotContain(Assert.Single(seen!.Tiles).Parcels, p => p.OwnerId == annaId);
    }

    private static async Task<int> DeleteRequestedAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AccountDeletion>().ProcessRequestedAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Deletion_needs_sign_in()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);

        var response = await api.CreateClient().DeleteAsync("/me", Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Все столбцы-идентификаторы схемы <c>app</c> — и одиночные (<c>uuid</c>), и списки (<c>uuid[]</c>), — где встречается
    /// этот номер: «таблица.столбец».
    /// </summary>
    private async Task<List<string>> TablesMentioningAsync(Guid id)
    {
        await using var db = database.CreateContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(Cancel);
        var columns = new List<(string Table, string Column, bool IsList)>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT table_name, column_name, udt_name FROM information_schema.columns WHERE table_schema = 'app' AND udt_name IN ('uuid', '_uuid')";
            await using var reader = await list.ExecuteReaderAsync(Cancel);
            while (await reader.ReadAsync(Cancel))
            {
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2) == "_uuid"));
            }
        }

        Assert.Contains(("parcels", "owner_id", false), columns); // проверка действительно видит таблицы
        Assert.Contains(("parcels", "loss_attackers", true), columns); // и списки номеров
        var found = new List<string>();
        foreach (var (table, column, isList) in columns.OrderBy(c => c.Table).ThenBy(c => c.Column))
        {
            await using var count = connection.CreateCommand();
            var condition = isList ? $"@id = ANY(\"{column}\")" : $"\"{column}\" = @id";
            count.CommandText = $"SELECT count(*) FROM app.\"{table}\" WHERE {condition}";
            var parameter = count.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = id;
            count.Parameters.Add(parameter);
            if (Convert.ToInt64(await count.ExecuteScalarAsync(Cancel)) > 0)
            {
                found.Add($"{table}.{column}");
            }
        }

        return found;
    }

    private async Task<double> LandAreaAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        var parcels = await db.Parcels.Where(p => p.OwnerId == userId).Select(p => p.Geometry).ToListAsync(Cancel);
        return parcels.Sum(g => g.Area);
    }

    private async Task<long> TileVersionAsync(TileKey tile)
    {
        await using var db = database.CreateContext();
        return await db.TileVersions
            .Where(v => v.League == Gorodki.Domain.Leagues.League.Run && v.TileX == tile.X && v.TileY == tile.Y)
            .Select(v => v.Version)
            .SingleAsync(Cancel);
    }
}
