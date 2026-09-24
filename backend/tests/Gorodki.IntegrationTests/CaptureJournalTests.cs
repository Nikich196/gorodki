using Gorodki.Api.Features.Captures;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using static Gorodki.IntegrationTests.RunRequests;
using static Gorodki.IntegrationTests.Walks;

namespace Gorodki.IntegrationTests;

/// <summary>
/// Журнал захватов на настоящей базе (PLAN.md, §7.3, шаг B.5): TWKB совпадает с PostGIS, журнал пишется вместе с землёй,
/// по нему из базы можно откатить захват, через неделю он стирается.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CaptureJournalTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static TheoryData<string> Geometries =>
    [
        "POLYGON((684123.4 5775987.6, 684223.7 5775987.6, 684223.7 5776068.3, 684123.4 5776068.3, 684123.4 5775987.6),"
            + " (684133.5 5775997.8, 684133.5 5776007.8, 684153.8 5776007.8, 684153.8 5775997.8, 684133.5 5775997.8))",
        "MULTIPOLYGON(((684000 5775000, 684100.1 5775000, 684100.1 5775100.1, 684000 5775000)),"
            + " ((684500.5 5775300.1, 684600 5775300.1, 684600 5775400, 684500.5 5775300.1)))",
        "POLYGON EMPTY",
    ];

    [Theory]
    [MemberData(nameof(Geometries))]
    public async Task Twkb_is_byte_for_byte_what_PostGIS_writes_and_reads(string wkt)
    {
        database.RequireDatabase();
        var geometry = new WKTReader(GeoOps.Factory.GeometryServices).Read(wkt);
        await using var db = database.CreateContext();

        // Фигура передаётся текстом: объект NTS Npgsql отправил бы пустой многоугольник трёхмерным, и PostGIS добавил бы
        // к нему байт размерности Z — сравнение было бы не того же самого.
        var postgis = await db.Database
            .SqlQuery<byte[]>($"SELECT extensions.st_astwkb(extensions.st_geomfromtext({wkt}), 1) AS \"Value\"")
            .SingleAsync(Cancel);
        var ours = Twkb.Write(geometry);
        var readByPostgis = await db.Database
            .SqlQuery<byte[]>($"SELECT extensions.st_asbinary(extensions.st_geomfromtwkb({ours})) AS \"Value\"")
            .SingleAsync(Cancel);

        Assert.Equal(Convert.ToHexString(postgis), Convert.ToHexString(ours));
        Assert.True(Twkb.Read(postgis).EqualsExact(geometry));
        Assert.True(new WKBReader(GeoOps.Factory.GeometryServices).Read(readByPostgis).EqualsExact(geometry));
    }

    [Fact]
    public async Task Applied_capture_is_journaled_and_can_be_rolled_back_from_the_journal()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, borisId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));
        var borisClaim = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await ProcessAsync(api, borisClaim.RunId);

        await using var db = database.CreateContext();
        var journal = await CaptureJournal.LoadAsync(db, borisClaim.CaptureId, Cancel);
        var capture = await db.Captures.AsNoTracking().SingleAsync(c => c.Id == borisClaim.CaptureId, Cancel);

        // След — это взятое у Анны и ничьё: вся петля Бориса, около 10 000 м².
        Assert.NotEmpty(journal);
        Assert.InRange(journal.Sum(c => c.Footprint.Area), 9_500, 10_500);
        Assert.All(journal, c => Assert.Equal(Utm34.Srid, c.Footprint.SRID));
        Assert.Contains(journal.SelectMany(c => c.Before), p => p.State.OwnerId == annaId);
        Assert.All(journal.SelectMany(c => c.After), p => Assert.Equal(borisId, p.State.OwnerId));
        Assert.All(
            await db.CaptureJournal.AsNoTracking().Where(j => j.CaptureId == borisClaim.CaptureId).ToListAsync(Cancel),
            j => Assert.Equal(capture.AppliedAt, j.AppliedAt));

        // Состояние из журнала совпадает с землёй в базе до миллисекунд — иначе откат счёл бы землю «тронутой».
        var map = await MapOfAsync(db, journal.Select(c => c.Tile));
        var restore = map.Restore(journal);

        Assert.Empty(TerritoryInvariants.Check(map));
        Assert.InRange(restore.SkippedArea, 0, 1);
        Assert.InRange(map.AreaOf(annaId), 9_500, 10_500);
        Assert.Equal(0, map.AreaOf(borisId), 1);
    }

    [Fact]
    public async Task Capture_records_the_rows_it_replaced_exactly_as_PostGIS_stored_them_and_the_rows_it_wrote()
    {
        // Точный откат (аудит BE-01) возвращает удалённые строки из журнала — значит, контур в журнале обязан быть ровно
        // тем, что лежал в parcels: побайтово тот же TWKB, что выдаёт сама PostGIS по сохранённому куску. А вставленные
        // строки — с номерами, которые дала база (identity), а не нулями до сохранения.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        api.Time.Advance(TimeSpan.FromMinutes(30));
        long annaParcel;
        byte[] postgisTwkb;
        Polygon stored;
        await using (var db = database.CreateContext())
        {
            var parcel = await db.Parcels.AsNoTracking().SingleAsync(p => p.OwnerId == annaId, Cancel);
            (annaParcel, stored) = (parcel.Id, parcel.Geometry);
            postgisTwkb = await db.Database
                .SqlQuery<byte[]>($"SELECT extensions.st_astwkb(geometry, 1) AS \"Value\" FROM app.parcels WHERE id = {annaParcel}")
                .SingleAsync(Cancel);
        }

        var claim = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        Assert.Equal(1, await ProcessAsync(api, claim.RunId));

        await using var check = database.CreateContext();
        var capture = await check.Captures.AsNoTracking().SingleAsync(c => c.Id == claim.CaptureId, Cancel);
        var rows = await check.CaptureJournalParcels.AsNoTracking().Where(r => r.CaptureId == claim.CaptureId).ToListAsync(Cancel);
        var parcels = await check.Parcels.AsNoTracking().Where(p => p.TileX == tile.X && p.TileY == tile.Y).ToListAsync(Cancel);

        var replaced = Assert.Single(rows, r => r.Replaced);
        Assert.Equal(annaParcel, replaced.ParcelId);
        Assert.Equal(Convert.ToHexString(postgisTwkb), Convert.ToHexString(replaced.Geometry!));
        Assert.True(Twkb.Read(replaced.Geometry!).EqualsExact(stored));
        Assert.DoesNotContain(parcels, p => p.Id == annaParcel); // удалённой строки в parcels больше нет

        // Все куски тайла сейчас — вставленные захватом (остаток Анны и земля Бориса), и строки журнала — ровно они.
        var written = rows.Where(r => !r.Replaced).ToList();
        Assert.Equal(parcels.Select(p => p.Id).Order(), written.Select(w => w.ParcelId).Order());
        foreach (var row in written)
        {
            var parcel = parcels.Single(p => p.Id == row.ParcelId);
            Assert.Equal(
                (parcel.OwnerId, parcel.Level, parcel.LastVisitAt, parcel.LastLevelUpAt, parcel.ShieldUntil, parcel.SiegeUntil, parcel.LossWindowSince),
                (row.OwnerId, row.Level, row.LastVisitAt, row.LastLevelUpAt, row.ShieldUntil, row.SiegeUntil, row.LossWindowSince));
            Assert.Equal(parcel.LossAttackers, row.LossAttackers);
            Assert.Null(row.Geometry);
        }

        Assert.All(rows, r => Assert.Equal((capture.AppliedAt!.Value, tile.X, tile.Y), (r.AppliedAt, r.TileX, r.TileY)));
    }

    [Fact]
    public async Task Failing_to_write_the_exact_undo_rows_never_fails_the_capture()
    {
        // Строки точного отката — лучшее усилие: без них проекция пойдёт по граням следа, как раньше. Захват из-за них не
        // падает: ошибка записи откатывается до точки сохранения, земля, журнал и версия тайла остаются.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, annaId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        var claim = await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100));
        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION app.test_refuse_exact_undo() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'нет'; END $$;
                CREATE TRIGGER test_refuse_exact_undo BEFORE INSERT ON app.capture_journal_parcels
                    FOR EACH ROW EXECUTE FUNCTION app.test_refuse_exact_undo();
                """,
                Cancel);
        }

        try
        {
            Assert.Equal(1, await ProcessAsync(api, claim.RunId));
        }
        finally
        {
            await using var db = database.CreateContext();
            await db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER test_refuse_exact_undo ON app.capture_journal_parcels; DROP FUNCTION app.test_refuse_exact_undo();",
                CancellationToken.None);
        }

        await using var check = database.CreateContext();
        Assert.Equal(CaptureStatus.Applied, (await check.Captures.AsNoTracking().SingleAsync(c => c.Id == claim.CaptureId, Cancel)).Status);
        Assert.True(await check.Parcels.AnyAsync(p => p.OwnerId == annaId, Cancel));
        Assert.True(await check.CaptureJournal.AnyAsync(j => j.CaptureId == claim.CaptureId, Cancel));
        Assert.False(await check.CaptureJournalParcels.AnyAsync(r => r.CaptureId == claim.CaptureId, Cancel));
        Assert.True(await check.TileVersions.AnyAsync(v => v.TileX == tile.X && v.TileY == tile.Y && v.Version >= 1, Cancel));
    }

    [Fact]
    public async Task Journal_older_than_a_week_is_pruned()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var claim = await WalkAndClaimAsync(Cancel, api, client, Square(NewArea(), 0, 0, 100));
        await ProcessAsync(api, claim.RunId);
        await using (var db = database.CreateContext())
        {
            Assert.True(await db.CaptureJournalPieces.AnyAsync(p => p.CaptureId == claim.CaptureId, Cancel));
        }

        api.Time.Advance(CaptureProcessor.JournalRetention - TimeSpan.FromHours(1));
        Assert.Equal(0, await PruneAsync(api, claim.CaptureId));
        api.Time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, await PruneAsync(api, claim.CaptureId));

        await using (var db = database.CreateContext())
        {
            Assert.False(await db.CaptureJournalPieces.AnyAsync(p => p.CaptureId == claim.CaptureId, Cancel));
            Assert.True(await db.Captures.AnyAsync(c => c.Id == claim.CaptureId, Cancel)); // сам захват остаётся
        }
    }

    [Fact]
    public async Task Exact_undo_rows_are_pruned_only_after_the_capture_can_no_longer_be_hidden()
    {
        // Строки точного отката живут недолго (CaptureProcessor.ExactUndoRetention): пока захват скрыт, они нужны проекции,
        // потом — нет, а контуры занимают место. Захват применён под конец 5-минутного шага: скрыт он дольше всего — до
        // задержки плюс шаг. Чистка в это время строк не трогает, и проекция всё ещё точная.
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (anna, _) = await api.CreatePlayerClientAsync();
        var (boris, _) = await api.CreatePlayerClientAsync();
        var (_, veraId) = await api.CreatePlayerClientAsync();
        var area = NewArea();
        var tile = TileKey.Of(WalkOrigin.X + area.X + 50, WalkOrigin.Y + area.Y + 50);
        await ProcessAsync(api, (await WalkAndClaimAsync(Cancel, api, anna, Square(area, 0, 0, 100))).RunId);
        var step = Gorodki.Api.Features.Territory.TerritoryReader.RevealStep;
        api.Time.Advance(Gorodki.Api.Features.Territory.TerritoryReader.PublicDelay + step); // захват Анны публичен
        var intoStep = TimeSpan.FromTicks(api.Time.GetUtcNow().UtcTicks % step.Ticks);
        api.Time.Advance(step - intoStep + TimeSpan.FromMinutes(4)); // захват Бориса — в конце шага
        var claim = await WalkAndClaimAsync(Cancel, api, boris, Square(area, 50, 0, 100));
        await ProcessAsync(api, claim.RunId);

        api.Time.Advance(Gorodki.Api.Features.Territory.TerritoryReader.PublicDelay + TimeSpan.FromSeconds(30)); // ещё скрыт
        await PruneAsync(api, claim.CaptureId);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var reader = scope.ServiceProvider.GetRequiredService<Gorodki.Api.Features.Territory.TerritoryReader>();
            await reader.ReadAsync(
                Gorodki.Domain.Leagues.League.Run, [(tile, null)], new Gorodki.Api.Features.Territory.TerritoryViewer(veraId, Immediate: false), Cancel);
            Assert.Equal((1, 0), (reader.Projections.Exact, reader.Projections.Fallback));
        }

        var retention = CaptureProcessor.ExactUndoRetention(Gorodki.Api.Features.Territory.TerritoryReader.PublicDelay);
        api.Time.Advance(retention - Gorodki.Api.Features.Territory.TerritoryReader.PublicDelay - TimeSpan.FromMinutes(1));
        await PruneAsync(api, claim.CaptureId);
        await using (var db = database.CreateContext())
        {
            Assert.True(await db.CaptureJournalParcels.AnyAsync(r => r.CaptureId == claim.CaptureId, Cancel));
        }

        api.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, await PruneAsync(api, claim.CaptureId)); // журнал захвата — неделю, для отката нарушителя
        await using (var db = database.CreateContext())
        {
            Assert.False(await db.CaptureJournalParcels.AnyAsync(r => r.CaptureId == claim.CaptureId, Cancel));
            Assert.True(await db.CaptureJournal.AnyAsync(j => j.CaptureId == claim.CaptureId, Cancel));
        }
    }

    /// <summary>Чистит журнал; возвращает, сколько тайлов этого захвата было стёрто.</summary>
    private async Task<int> PruneAsync(ApiFactory api, Guid captureId)
    {
        await using var db = database.CreateContext();
        var before = await db.CaptureJournal.CountAsync(j => j.CaptureId == captureId, Cancel);
        await using var scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CaptureProcessor>().PruneJournalAsync(Cancel);
        return before - await db.CaptureJournal.CountAsync(j => j.CaptureId == captureId, Cancel);
    }

    /// <summary>Карта тайлов из базы со всеми полями состояния.</summary>
    private async Task<TerritoryMap> MapOfAsync(AppDbContext db, IEnumerable<TileKey> tiles)
    {
        var keys = tiles.ToList();
        var xs = keys.Select(t => t.X).ToList();
        var ys = keys.Select(t => t.Y).ToList();
        var parcels = await db.Parcels.AsNoTracking()
            .Where(p => p.League == Gorodki.Domain.Leagues.League.Run && xs.Contains(p.TileX) && ys.Contains(p.TileY))
            .ToListAsync(Cancel);
        var map = new TerritoryMap();
        map.Load(parcels
            .Where(p => keys.Contains(new TileKey(p.TileX, p.TileY)))
            .Select(p => new Parcel(
                new TileKey(p.TileX, p.TileY),
                p.Geometry,
                new ParcelState
                {
                    OwnerId = p.OwnerId,
                    Level = p.Level,
                    LastVisitAt = p.LastVisitAt,
                    LastLevelUpAt = p.LastLevelUpAt,
                    ShieldUntil = p.ShieldUntil,
                    SiegeUntil = p.SiegeUntil,
                    LossWindowSince = p.LossWindowSince,
                    LossAttackers = AttackerSet.Of(p.LossAttackers),
                })));
        return map;
    }
}
