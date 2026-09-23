using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "app");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:extensions.postgis", ",,")
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "game_configs",
                schema: "app",
                columns: table => new
                {
                    version = table.Column<int>(type: "integer", nullable: false),
                    json = table.Column<string>(type: "jsonb", nullable: false),
                    active_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_configs", x => x.version);
                });

            migrationBuilder.CreateTable(
                name: "invites",
                schema: "app",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: false),
                    used_count = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invites", x => x.code);
                    table.CheckConstraint("ck_invites_uses", "used_count >= 0 AND used_count <= max_uses");
                });

            migrationBuilder.CreateTable(
                name: "tile_versions",
                schema: "app",
                columns: table => new
                {
                    league = table.Column<short>(type: "smallint", nullable: false),
                    tile_x = table.Column<int>(type: "integer", nullable: false),
                    tile_y = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tile_versions", x => new { x.league, x.tile_x, x.tile_y });
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    google_subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    apple_subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    display_name = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    color_index = table.Column<short>(type: "smallint", nullable: false),
                    role = table.Column<short>(type: "smallint", nullable: false),
                    age_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consent_version = table.Column<int>(type: "integer", nullable: true),
                    consented_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    public_profile = table.Column<bool>(type: "boolean", nullable: false),
                    invite_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deletion_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_color_index", "color_index BETWEEN 0 AND 11");
                });

            migrationBuilder.CreateTable(
                name: "parcels",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    league = table.Column<short>(type: "smallint", nullable: false),
                    tile_x = table.Column<int>(type: "integer", nullable: false),
                    tile_y = table.Column<int>(type: "integer", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    level = table.Column<short>(type: "smallint", nullable: false),
                    last_visit_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_level_up_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    shield_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    siege_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    geometry = table.Column<Polygon>(type: "geometry(Polygon, 32634)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parcels", x => x.id);
                    table.CheckConstraint("ck_parcels_geometry_valid", "extensions.st_isvalid(geometry)");
                    table.CheckConstraint("ck_parcels_level", "level BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "fk_parcels_users_owner_id",
                        column: x => x.owner_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "runs",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    league = table.Column<short>(type: "smallint", nullable: false),
                    source = table.Column<short>(type: "smallint", nullable: false),
                    config_version = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    processed_seq = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_runs_game_configs_config_version",
                        column: x => x.config_version,
                        principalSchema: "app",
                        principalTable: "game_configs",
                        principalColumn: "version",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_runs_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "captures",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    league = table.Column<short>(type: "smallint", nullable: false),
                    start_seq = table.Column<int>(type: "integer", nullable: false),
                    end_seq = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    reject_reason = table.Column<short>(type: "smallint", nullable: true),
                    area_square_meters = table.Column<double>(type: "double precision", nullable: false),
                    shape = table.Column<Geometry>(type: "geometry(Geometry, 32634)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_captures", x => x.id);
                    table.ForeignKey(
                        name: "fk_captures_runs_run_id",
                        column: x => x.run_id,
                        principalSchema: "app",
                        principalTable: "runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "run_chunks",
                schema: "app",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seq = table.Column<int>(type: "integer", nullable: false),
                    last_seq = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    points = table.Column<byte[]>(type: "bytea", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_chunks", x => new { x.run_id, x.first_seq });
                    table.CheckConstraint("ck_run_chunks_seq", "first_seq >= 0 AND last_seq >= first_seq");
                    table.ForeignKey(
                        name: "fk_run_chunks_runs_run_id",
                        column: x => x.run_id,
                        principalSchema: "app",
                        principalTable: "runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_captures_run_id",
                schema: "app",
                table: "captures",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_captures_user_id_created_at",
                schema: "app",
                table: "captures",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_parcels_geometry",
                schema: "app",
                table: "parcels",
                column: "geometry")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_parcels_league_tile_x_tile_y",
                schema: "app",
                table: "parcels",
                columns: new[] { "league", "tile_x", "tile_y" });

            migrationBuilder.CreateIndex(
                name: "ix_parcels_owner_id",
                schema: "app",
                table: "parcels",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_run_chunks_run_id_content_hash",
                schema: "app",
                table: "run_chunks",
                columns: new[] { "run_id", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runs_config_version",
                schema: "app",
                table: "runs",
                column: "config_version");

            migrationBuilder.CreateIndex(
                name: "ix_runs_user_id_started_at",
                schema: "app",
                table: "runs",
                columns: new[] { "user_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_users_apple_subject",
                schema: "app",
                table: "users",
                column: "apple_subject",
                unique: true,
                filter: "apple_subject IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_google_subject",
                schema: "app",
                table: "users",
                column: "google_subject",
                unique: true,
                filter: "google_subject IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_name",
                schema: "app",
                table: "users",
                column: "normalized_name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "captures",
                schema: "app");

            migrationBuilder.DropTable(
                name: "invites",
                schema: "app");

            migrationBuilder.DropTable(
                name: "parcels",
                schema: "app");

            migrationBuilder.DropTable(
                name: "run_chunks",
                schema: "app");

            migrationBuilder.DropTable(
                name: "tile_versions",
                schema: "app");

            migrationBuilder.DropTable(
                name: "runs",
                schema: "app");

            migrationBuilder.DropTable(
                name: "game_configs",
                schema: "app");

            migrationBuilder.DropTable(
                name: "users",
                schema: "app");
        }
    }
}
