using Gorodki.Api.Features.Config;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Config;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Gorodki.IntegrationTests;

/// <summary>Схема v1 на настоящей PostGIS: миграции применяются, геометрия хранится так, как требует движок участков.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class SchemaTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PostGIS_is_version_3_3_as_on_Supabase()
    {
        database.RequireDatabase();
        await using var db = database.CreateContext();

        var version = await db.Database
            .SqlQueryRaw<string>("SELECT extensions.postgis_full_version() AS \"Value\"")
            .SingleAsync(Cancel);

        // Функции PostGIS 3.4+ (ST_Coverage* и др.) здесь не существуют — как и на Supabase.
        Assert.Contains("POSTGIS=\"3.3.", version, StringComparison.Ordinal);
        TestContext.Current.SendDiagnosticMessage(version);
    }

    [Fact]
    public async Task All_tables_are_in_schema_app()
    {
        database.RequireDatabase();
        await using var db = database.CreateContext();

        var tables = await db.Database
            .SqlQueryRaw<string>(
                "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'app' ORDER BY table_name")
            .ToListAsync(Cancel);

        Assert.Equal(
            ["capture_journal", "capture_journal_pieces", "capture_rollbacks", "captures", "ef_migrations_history", "fog_tiles", "game_configs", "invites", "parcels", "privacy_zones", "refresh_tokens", "run_chunks", "runs", "seasons", "tile_versions", "users"],
            tables);
    }

    [Fact]
    public async Task Parcel_geometry_round_trips_exactly_and_is_found_by_spatial_query()
    {
        database.RequireDatabase();
        var owner = await CreateUserAsync("roundtrip");
        var square = Square(684_100, 5_775_100, 100);

        await using (var db = database.CreateContext())
        {
            db.Parcels.Add(NewParcel(owner, square));
            await db.SaveChangesAsync(Cancel);
        }

        await using (var db = database.CreateContext())
        {
            var probe = GeoOps.Factory.CreatePoint(new Coordinate(684_150, 5_775_150));
            var stored = await db.Parcels.SingleAsync(p => p.OwnerId == owner && p.Geometry.Intersects(probe), Cancel);

            Assert.Equal(Utm34.Srid, stored.Geometry.SRID);
            Assert.True(stored.Geometry.EqualsExact(square));
            Assert.True(GeoOps.IsOnGrid(stored.Geometry));
        }
    }

    [Fact]
    public async Task Invalid_geometry_is_refused_by_the_database_itself()
    {
        database.RequireDatabase();
        var owner = await CreateUserAsync("bowtie");
        // «Бабочка» — самопересекающийся контур: движок такого не пишет, но если ошибётся — база не примет.
        var bowTie = GeoOps.Factory.CreatePolygon(
        [
            new Coordinate(684_200, 5_775_200), new Coordinate(684_300, 5_775_300), new Coordinate(684_300, 5_775_200),
            new Coordinate(684_200, 5_775_300), new Coordinate(684_200, 5_775_200),
        ]);

        await using var db = database.CreateContext();
        db.Parcels.Add(NewParcel(owner, bowTie));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        Assert.Contains("ck_parcels_geometry_valid", error.InnerException?.Message ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_chunk_content_is_stored_only_once()
    {
        database.RequireDatabase();
        var owner = await CreateUserAsync("chunks");
        var runId = Guid.CreateVersion7();
        var hash = new byte[32];
        hash[0] = 7;

        await using (var db = database.CreateContext())
        {
            await EnsureConfigAsync(db);
            db.Runs.Add(new RunEntity
            {
                Id = runId,
                UserId = owner,
                League = League.Run,
                ConfigVersion = 1,
                StartedAt = DateTimeOffset.UtcNow,
                AppVersion = "test",
            });
            db.RunChunks.Add(new RunChunkEntity
            {
                RunId = runId,
                FirstSeq = 0,
                LastSeq = 9,
                ContentHash = hash,
                Points = [1, 2, 3],
                ReceivedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Cancel);
        }

        await using (var db = database.CreateContext())
        {
            // Тот же кусок пришёл повторно (сеть моргнула) под другим номером начала — база не даст сохранить дубль.
            db.RunChunks.Add(new RunChunkEntity
            {
                RunId = runId,
                FirstSeq = 10,
                LastSeq = 19,
                ContentHash = hash,
                Points = [1, 2, 3],
                ReceivedAt = DateTimeOffset.UtcNow,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        }
    }

    [Fact]
    public async Task Overlapping_chunks_of_one_run_are_refused_by_the_database()
    {
        database.RequireDatabase();
        var runId = await CreateRunAsync(await CreateUserAsync("overlap"), RunStatus.Finished);

        await AddChunkAsync(runId, 0, 9);
        var overlap = await Assert.ThrowsAsync<DbUpdateException>(() => AddChunkAsync(runId, 5, 14));
        await AddChunkAsync(runId, 10, 19); // соседний кусок — можно

        Assert.Equal(Npgsql.PostgresErrorCodes.ExclusionViolation, Assert.IsType<Npgsql.PostgresException>(overlap.InnerException).SqlState);
    }

    [Fact]
    public async Task Player_has_at_most_one_active_run()
    {
        database.RequireDatabase();
        var owner = await CreateUserAsync("active");
        await CreateRunAsync(owner, RunStatus.Active);
        await CreateRunAsync(owner, RunStatus.Finished);

        var second = await Assert.ThrowsAsync<DbUpdateException>(() => CreateRunAsync(owner, RunStatus.Active));

        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, Assert.IsType<Npgsql.PostgresException>(second.InnerException).SqlState);
    }

    // MARK: — вспомогательное

    private async Task<Guid> CreateRunAsync(Guid owner, RunStatus status)
    {
        await using var db = database.CreateContext();
        await EnsureConfigAsync(db);
        var run = new RunEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = owner,
            League = League.Run,
            ConfigVersion = 1,
            StartedAt = DateTimeOffset.UtcNow,
            Status = status,
            AppVersion = "test",
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync(Cancel);
        return run.Id;
    }

    private async Task AddChunkAsync(Guid runId, int firstSeq, int lastSeq)
    {
        await using var db = database.CreateContext();
        db.RunChunks.Add(new RunChunkEntity
        {
            RunId = runId,
            FirstSeq = firstSeq,
            LastSeq = lastSeq,
            ContentHash = Guid.NewGuid().ToByteArray(),
            Points = [1],
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Cancel);
    }

    private async Task<Guid> CreateUserAsync(string name)
    {
        await using var db = database.CreateContext();
        var id = Guid.CreateVersion7();
        var nick = $"{name}-{id.ToString("N")[^8..]}"; // хвост UUIDv7 случайный, начало — время
        db.Users.Add(new UserEntity
        {
            Id = id,
            DisplayName = nick,
            NormalizedName = nick.ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Cancel);
        return id;
    }

    private async Task EnsureConfigAsync(AppDbContext db)
    {
        if (!await db.GameConfigs.AnyAsync(c => c.Version == 1, Cancel))
        {
            // Так же, как версию 1 создаёт сервер (GameConfigStore): числа по умолчанию, действует «всегда».
            db.GameConfigs.Add(new GameConfigEntity
            {
                Version = 1,
                Json = GameConfig.Default.ToJson(),
                ActiveFrom = GameConfigStore.FirstVersionActiveFrom,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    private static ParcelEntity NewParcel(Guid owner, Polygon geometry)
    {
        var now = DateTimeOffset.UtcNow;
        var tile = TileKey.Of(geometry.EnvelopeInternal.MinX, geometry.EnvelopeInternal.MinY);
        return new ParcelEntity
        {
            League = League.Run,
            TileX = tile.X,
            TileY = tile.Y,
            OwnerId = owner,
            Level = 1,
            LastVisitAt = now,
            LastLevelUpAt = now,
            Geometry = geometry,
        };
    }

    private static Polygon Square(double x, double y, double size) =>
        GeoOps.Factory.CreatePolygon(
        [
            new Coordinate(x, y), new Coordinate(x + size, y), new Coordinate(x + size, y + size),
            new Coordinate(x, y + size), new Coordinate(x, y),
        ]);
}
