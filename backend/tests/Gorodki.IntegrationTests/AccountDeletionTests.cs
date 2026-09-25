using System.Net;
using System.Net.Http.Json;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Fog;
using Gorodki.Api.Features.Me;
using Gorodki.Api.Features.Realtime;
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
        var (vera, veraId) = await api.CreatePlayerClientAsync();
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
        // Анна успела снять уровень у земли Бориса: её номер — в списке атакующих его участка и в журнале (в земле до/после
        // и в строках точного отката).
        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE app.parcels SET loss_attackers = ARRAY[{annaId}] WHERE owner_id = {borisId}", Cancel);
            await db.Database.ExecuteSqlAsync($"UPDATE app.capture_journal_pieces SET loss_attackers = ARRAY[{annaId}] WHERE owner_id = {borisId}", Cancel);
            Assert.True(await db.Database.ExecuteSqlAsync(
                $"UPDATE app.capture_journal_parcels SET loss_attackers = ARRAY[{annaId}] WHERE owner_id = {borisId}", Cancel) > 0);
            Assert.True(await db.CaptureJournalParcels.AnyAsync(r => r.OwnerId == annaId, Cancel)); // кусок Анны, удалённый захватом
        }

        var versionBefore = await TileVersionAsync(tile);
        Assert.True(await DeleteRequestedAsync(api) >= 1);

        // Полнота: номер Анны остался только в журнале чужого захвата — «земля до» захвата Бориса.
        Assert.Equal(["capture_journal_pieces.owner_id"], await TablesMentioningAsync(annaId));
        Assert.InRange(await LandAreaAsync(borisId), 9_700, 10_300); // земля Бориса не тронута
        Assert.True(await TileVersionAsync(tile) > versionBefore); // у соседей карта обновится

        // Захват Бориса ещё скрыт задержкой (20 минут): проекция вернула бы Вере землю Анны — но удалённый на карте
        // не появляется. Строки точного отката вычищены так же, как земля, и захват откатывается точно: на месте куска Анны —
        // ничья земля, как в мире без захвата.
        var seen = await vera.GetFromJsonAsync<TerritoryResponse>($"/territory?league=run&tiles={tile.X}:{tile.Y}", Json, Cancel);
        Assert.Empty(Assert.Single(seen!.Tiles).Parcels);
        await using var readerScope = api.Services.CreateAsyncScope();
        var reader = readerScope.ServiceProvider.GetRequiredService<TerritoryReader>();
        await reader.ReadAsync(Gorodki.Domain.Leagues.League.Run, [(tile, null)], new TerritoryViewer(veraId, Immediate: false), Cancel);
        Assert.Equal((1, 0), (reader.Projections.Exact, reader.Projections.Fallback));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // строк точного отката нет (захват записан до них) — проекция возвращала землю Анны по журналу
    public async Task Tile_where_a_hidden_capture_took_all_of_the_deleted_accounts_land_gets_a_new_version(bool exactRows)
    {
        // Борис забрал весь квадрат Анны, его захват ещё скрыт, и тут стирается аккаунт Анны. Кусков Анны в parcels уже
        // нет, но Вере проекция их возвращала — как в мире без захвата. После удаления не вернёт, и у Веры тайл должен
        // обновиться (видимая версия +1, подсказка): иначе с кэшем она до раскрытия видела бы землю удалённого аккаунта, а
        // этот тайл — единственный тайл Анны без новой версии — выдал бы, где скрытый захват.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var (vera, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 20, 20, 80))).RunId);
        Assert.Equal(HttpStatusCode.Accepted, (await anna.DeleteAsync("/me", Cancel)).StatusCode);
        api.Time.Advance(TimeSpan.FromMinutes(25));
        var claim = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 0, 0, 120));
        Assert.Equal(1, await Walks.ProcessAsync(api, claim.RunId));
        Assert.Equal(0.0, await LandAreaAsync(annaId)); // у Анны не осталось ни куска
        if (!exactRows)
        {
            await using var db = database.CreateContext();
            Assert.True(await db.CaptureJournalParcels.Where(r => r.CaptureId == claim.CaptureId).ExecuteDeleteAsync(Cancel) > 0);
        }

        var cached = Assert.Single((await vera.GetFromJsonAsync<TerritoryResponse>(
            $"/territory?league=run&tiles={tile.X}:{tile.Y}", Json, Cancel))!.Tiles);
        Assert.Equal(annaId, Assert.Single(cached.Parcels).OwnerId); // захват Бориса скрыт — Вера видит квадрат Анны
        var hints = api.Services.GetRequiredService<RealtimeHints>();
        while (hints.Reader.TryRead(out _))
        {
        }

        Assert.True(await DeleteRequestedAsync(api) >= 1);
        Assert.False(await ExistsAsync(annaId));

        var after = await vera.GetFromJsonAsync<TerritoryResponse>(
            $"/territory?league=run&tiles={tile.X}:{tile.Y}@{cached.Version}", Json, Cancel);
        var refreshed = Assert.Single(after!.Tiles); // не «без изменений»
        Assert.Equal(cached.Version + 1, refreshed.Version);
        Assert.Empty(refreshed.Parcels); // на месте Анны — ничья земля, как без захвата
        var sent = new List<RealtimeHint>();
        while (hints.Reader.TryRead(out var hint))
        {
            sent.Add(hint);
        }

        Assert.Contains(sent, h => h is TilesChangedHint { UserId: null } changed && changed.Tiles.Contains(tile));
    }

    [Fact]
    public async Task Deletion_waits_for_the_tiles_where_the_account_is_only_in_the_lists_of_attackers()
    {
        // Номер Анны — в списке снявших уровень у куска Бориса, в тайле, где своей земли у Анны нет. Захват в этом тайле,
        // прочитавший кусок до чистки, записал бы номер Анны обратно уже после неё. Поэтому удаление берёт блокировку и этого
        // тайла, как захват: пока тайл держит захват (здесь — другое соединение), аккаунт не стирается — удаление ждёт
        // (lock_timeout 5 с) и отступает до следующего прохода.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var borisArea = NewArea();
        var borisTile = TileKey.Of(WalkOrigin.X + borisArea.X + 50, WalkOrigin.Y + borisArea.Y + 50);
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100))).RunId);
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, boris, Square(borisArea, 0, 0, 100))).RunId);
        await using (var db = database.CreateContext())
        {
            Assert.True(await db.Database.ExecuteSqlAsync(
                $"UPDATE app.parcels SET loss_attackers = ARRAY[{annaId}] WHERE owner_id = {borisId}", Cancel) > 0);
            Assert.False(await db.Parcels.AnyAsync(p => p.OwnerId == annaId && p.TileX == borisTile.X && p.TileY == borisTile.Y, Cancel));
        }

        Assert.Equal(HttpStatusCode.Accepted, (await anna.DeleteAsync("/me", Cancel)).StatusCode);
        api.Time.Advance(TimeSpan.FromMinutes(25));
        await using (var capture = database.CreateContext())
        {
            await using var transaction = await capture.Database.BeginTransactionAsync(Cancel);
            await capture.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock({100 + (int)Gorodki.Domain.Leagues.League.Run}, {borisTile.LockKey})", Cancel);

            await DeleteRequestedAsync(api);
            Assert.True(await ExistsAsync(annaId));
        }

        await DeleteRequestedAsync(api);
        Assert.False(await ExistsAsync(annaId));
        await using (var db = database.CreateContext())
        {
            Assert.False(await db.Parcels.AnyAsync(p => p.LossAttackers.Contains(annaId), Cancel));
        }
    }

    private static async Task<int> DeleteRequestedAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AccountDeletion>().ProcessRequestedAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Account_is_not_erased_before_its_last_capture_is_public_on_the_map()
    {
        // Граница — та же, что у публичной проекции (вниз до 5 минут). «Просто 20 минут» опередили бы её: стёртая земля
        // проступила бы у соседей раньше, чем они увидели бы сам захват.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var step = TerritoryReader.RevealStep;
        var intoStep = TimeSpan.FromTicks(api.Time.GetUtcNow().UtcTicks % step.Ticks);
        api.Time.Advance(step - intoStep + (step / 2)); // захват — посреди 5-минутного окна
        await Walks.ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(NewArea(), 0, 0, 100))).RunId);
        Assert.Equal(HttpStatusCode.Accepted, (await anna.DeleteAsync("/me", Cancel)).StatusCode);

        api.Time.Advance(TerritoryReader.PublicDelay + TimeSpan.FromSeconds(1));
        await DeleteRequestedAsync(api);
        Assert.True(await ExistsAsync(annaId)); // 20 минут прошло, а граница публичности до захвата не дошла

        api.Time.Advance(step / 2);
        await DeleteRequestedAsync(api);
        Assert.False(await ExistsAsync(annaId));
    }

    private async Task<bool> ExistsAsync(Guid userId)
    {
        await using var db = database.CreateContext();
        return await db.Users.AnyAsync(u => u.Id == userId, Cancel);
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
