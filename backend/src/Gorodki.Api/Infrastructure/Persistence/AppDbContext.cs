using Gorodki.Domain.Geo;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Gorodki.Api.Infrastructure.Persistence;

/// <summary>
/// База данных «Городков»: PostgreSQL 17 + PostGIS 3.3 (Supabase). Все таблицы — в схеме <c>app</c>,
/// PostGIS — в схеме <c>extensions</c>, как принято на Supabase (PLAN.md, §7.3).
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public const string Schema = "app";

    public DbSet<UserEntity> Users => Set<UserEntity>();

    public DbSet<InviteEntity> Invites => Set<InviteEntity>();

    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();

    public DbSet<GameConfigEntity> GameConfigs => Set<GameConfigEntity>();

    public DbSet<RunEntity> Runs => Set<RunEntity>();

    public DbSet<RunChunkEntity> RunChunks => Set<RunChunkEntity>();

    public DbSet<ParcelEntity> Parcels => Set<ParcelEntity>();

    public DbSet<CaptureEntity> Captures => Set<CaptureEntity>();

    public DbSet<TileVersionEntity> TileVersions => Set<TileVersionEntity>();

    public DbSet<FogTileEntity> FogTiles => Set<FogTileEntity>();

    /// <summary>
    /// Общие настройки подключения — и для сервера, и для инструментов миграций.
    /// Геометрия из базы читается на той же сетке 0,1 м, что и в движке участков (<see cref="GeoOps.Grid"/>).
    /// </summary>
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(WithSearchPath(connectionString), npgsql => ConfigureNpgsql(npgsql))
            .UseSnakeCaseNamingConvention();

    /// <summary>
    /// Тип <c>geometry</c> живёт в схеме <c>extensions</c>: без неё в пути поиска PostgreSQL его не найдёт.
    /// Если путь не задан в строке подключения, ставим свой.
    /// </summary>
    public static string WithSearchPath(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.SearchPath))
        {
            builder.SearchPath = $"{Schema},extensions,public";
        }

        return builder.ConnectionString;
    }

    private static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql)
    {
        npgsql.UseNetTopologySuite(precisionModel: GeoOps.Grid);
        npgsql.MigrationsHistoryTable("ef_migrations_history", Schema);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.HasPostgresExtension("extensions", "postgis");
        // Для запрета пересекающихся кусков забега: EXCLUDE по (run_id, диапазон номеров) — в миграции RunsUpload.
        model.HasPostgresExtension("extensions", "btree_gist");

        model.Entity<UserEntity>(user =>
        {
            user.HasKey(u => u.Id);
            user.Property(u => u.DisplayName).HasMaxLength(24);
            user.Property(u => u.NormalizedName).HasMaxLength(24);
            user.Property(u => u.GoogleSubject).HasMaxLength(255);
            user.Property(u => u.AppleSubject).HasMaxLength(255);
            user.Property(u => u.InviteCode).HasMaxLength(16);
            user.HasIndex(u => u.NormalizedName).IsUnique();
            user.HasIndex(u => u.GoogleSubject).IsUnique().HasFilter("google_subject IS NOT NULL");
            user.HasIndex(u => u.AppleSubject).IsUnique().HasFilter("apple_subject IS NOT NULL");
            user.ToTable(t => t.HasCheckConstraint("ck_users_color_index", "color_index BETWEEN 0 AND 11"));
        });

        model.Entity<RefreshTokenEntity>(token =>
        {
            token.HasKey(t => t.Id);
            token.HasOne<UserEntity>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.HasIndex(t => t.FamilyId);
        });

        model.Entity<InviteEntity>(invite =>
        {
            invite.HasKey(i => i.Code);
            invite.Property(i => i.Code).HasMaxLength(16);
            invite.Property(i => i.Note).HasMaxLength(200);
            invite.Property(i => i.Version).IsRowVersion();
            invite.ToTable(t => t.HasCheckConstraint("ck_invites_uses", "used_count >= 0 AND used_count <= max_uses"));
        });

        model.Entity<GameConfigEntity>(config =>
        {
            config.HasKey(c => c.Version);
            config.Property(c => c.Version).ValueGeneratedNever();
            config.Property(c => c.Json).HasColumnType("jsonb");
        });

        model.Entity<RunEntity>(run =>
        {
            run.HasKey(r => r.Id);
            run.HasOne<UserEntity>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            run.HasOne<GameConfigEntity>().WithMany().HasForeignKey(r => r.ConfigVersion).OnDelete(DeleteBehavior.Restrict);
            run.HasIndex(r => new { r.UserId, r.StartedAt });
            run.Property(r => r.AppVersion).HasMaxLength(32);
            // Один активный забег на игрока (PLAN.md, §3.2) — это гарантирует сама база.
            run.HasIndex(r => r.UserId).IsUnique().HasFilter("status = 0").HasDatabaseName("ux_runs_one_active_per_user");
            run.ToTable(t =>
            {
                t.HasCheckConstraint("ck_runs_last_seq", "last_seq IS NULL OR last_seq >= -1");
                t.HasCheckConstraint("ck_runs_counters", "chunk_count >= 0 AND stored_bytes >= 0");
            });
        });

        model.Entity<RunChunkEntity>(chunk =>
        {
            chunk.HasKey(c => new { c.RunId, c.FirstSeq });
            chunk.HasOne<RunEntity>().WithMany().HasForeignKey(c => c.RunId).OnDelete(DeleteBehavior.Cascade);
            chunk.HasIndex(c => new { c.RunId, c.ContentHash }).IsUnique();
            chunk.ToTable(t => t.HasCheckConstraint("ck_run_chunks_seq", "first_seq >= 0 AND last_seq >= first_seq"));
        });

        model.Entity<ParcelEntity>(parcel =>
        {
            parcel.HasKey(p => p.Id);
            parcel.Property(p => p.Id).UseIdentityAlwaysColumn();
            parcel.Property(p => p.Geometry).HasColumnType($"geometry(Polygon, {Utm34.Srid})");
            parcel.HasOne<UserEntity>().WithMany().HasForeignKey(p => p.OwnerId).OnDelete(DeleteBehavior.Cascade);
            parcel.HasIndex(p => new { p.League, p.TileX, p.TileY });
            parcel.HasIndex(p => p.OwnerId);
            parcel.HasIndex(p => p.Geometry).HasMethod("gist");
            parcel.ToTable(t =>
            {
                t.HasCheckConstraint("ck_parcels_level", "level BETWEEN 1 AND 3");
                // Неправильная геометрия в базу не попадает никогда — даже если в движке ошибка.
                t.HasCheckConstraint("ck_parcels_geometry_valid", "extensions.st_isvalid(geometry)");
            });
        });

        // Порядок применения захватов к карте: по нему карту можно переиграть (ночная перестройка, откат).
        model.HasSequence<long>("capture_apply_seq");

        model.Entity<CaptureEntity>(capture =>
        {
            capture.HasKey(c => c.Id);
            capture.Property(c => c.Id).ValueGeneratedNever();
            capture.Property(c => c.Shape).HasColumnType($"geometry(Geometry, {Utm34.Srid})");
            capture.Property(c => c.AreaByOutcome).HasColumnType("jsonb");
            capture.Property(c => c.ChangedTiles).HasColumnType("jsonb");
            capture.Property(c => c.RejectCode).HasMaxLength(48);
            capture.Property(c => c.LastError).HasMaxLength(500);

            // История захватов нужна для переигровки карты — удаление забега её не стирает.
            capture.HasOne<RunEntity>().WithMany().HasForeignKey(c => c.RunId).OnDelete(DeleteBehavior.Restrict);
            capture.HasIndex(c => new { c.RunId, c.ClaimNo }).IsUnique();
            capture.HasIndex(c => new { c.UserId, c.EffectiveAt }).HasFilter("status = 1");
            capture.HasIndex(c => new { c.Status, c.LeaseUntil }).HasFilter("status = 0");
            capture.ToTable(t =>
            {
                t.HasCheckConstraint("ck_captures_status", "status BETWEEN 0 AND 4");
                t.HasCheckConstraint("ck_captures_seq", "start_seq >= 0 AND end_seq > start_seq");
            });
        });

        model.Entity<TileVersionEntity>(tile =>
        {
            tile.HasKey(t => new { t.League, t.TileX, t.TileY });
        });

        model.Entity<FogTileEntity>(fog =>
        {
            fog.HasKey(f => new { f.UserId, f.Layer, f.Season, f.TileX, f.TileY });
            fog.HasOne<UserEntity>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
            fog.ToTable(t => t.HasCheckConstraint("ck_fog_tiles_cells", "cell_count BETWEEN 1 AND 65536"));
        });
    }
}

/// <summary>
/// Для инструмента миграций (<c>dotnet ef</c>): создаёт контекст без запуска сервера.
/// Строка подключения здесь не используется — миграции только генерируются, база не нужна.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(options, "Host=localhost;Database=gorodki_design;Username=design");
        return new AppDbContext(options.Options);
    }
}
