using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScoringAndSeasonRollover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "clean_start",
                schema: "app",
                table: "seasons",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reset_at",
                schema: "app",
                table: "seasons",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "touched_at",
                schema: "app",
                table: "parcels",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "touched_at",
                schema: "app",
                table: "capture_journal_pieces",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // «Касание владельца» (ParcelState.TouchedAt) у уже лежащей земли и в журнале — последний визит: до этой миграции
            // его меняли только взятие и освежение своим забегом (кланов и смены сезона ещё не было), так что это оно и есть.
            migrationBuilder.Sql("UPDATE app.parcels SET touched_at = last_visit_at;");
            migrationBuilder.Sql("UPDATE app.capture_journal_pieces SET touched_at = last_visit_at;");

            migrationBuilder.CreateTable(
                name: "score_events",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    league = table.Column<short>(type: "smallint", nullable: false),
                    season = table.Column<int>(type: "integer", nullable: true),
                    game_day = table.Column<DateOnly>(type: "date", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    points = table.Column<int>(type: "integer", nullable: false),
                    basis = table.Column<double>(type: "double precision", nullable: false),
                    capture_id = table.Column<Guid>(type: "uuid", nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    visible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_score_events", x => x.id);
                    table.CheckConstraint("ck_score_events_capture", "kind <> 1 OR capture_id IS NOT NULL");
                    table.CheckConstraint("ck_score_events_distance", "kind <> 2 OR run_id IS NOT NULL");
                    table.CheckConstraint("ck_score_events_kind", "kind BETWEEN 1 AND 2");
                    table.CheckConstraint("ck_score_events_values", "points >= 0 AND basis >= 0");
                    table.ForeignKey(
                        name: "fk_score_events_captures_capture_id",
                        column: x => x.capture_id,
                        principalSchema: "app",
                        principalTable: "captures",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_score_events_runs_run_id",
                        column: x => x.run_id,
                        principalSchema: "app",
                        principalTable: "runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_score_events_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                schema: "app",
                table: "seasons",
                keyColumn: "number",
                keyValue: 0,
                columns: new[] { "clean_start", "reset_at" },
                values: new object[] { true, null });

            migrationBuilder.UpdateData(
                schema: "app",
                table: "seasons",
                keyColumn: "number",
                keyValue: 1,
                columns: new[] { "clean_start", "reset_at" },
                values: new object[] { false, null });

            migrationBuilder.UpdateData(
                schema: "app",
                table: "seasons",
                keyColumn: "number",
                keyValue: 2,
                columns: new[] { "clean_start", "reset_at" },
                values: new object[] { false, null });

            migrationBuilder.CreateIndex(
                name: "ix_score_events_capture_id",
                schema: "app",
                table: "score_events",
                column: "capture_id",
                unique: true,
                filter: "capture_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_score_events_league_season_visible_at",
                schema: "app",
                table: "score_events",
                columns: new[] { "league", "season", "visible_at" });

            migrationBuilder.CreateIndex(
                name: "ix_score_events_user_id_league_game_day_kind",
                schema: "app",
                table: "score_events",
                columns: new[] { "user_id", "league", "game_day", "kind" });

            migrationBuilder.CreateIndex(
                name: "ux_score_events_distance_per_run",
                schema: "app",
                table: "score_events",
                column: "run_id",
                unique: true,
                filter: "kind = 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "score_events",
                schema: "app");

            migrationBuilder.DropColumn(
                name: "clean_start",
                schema: "app",
                table: "seasons");

            migrationBuilder.DropColumn(
                name: "reset_at",
                schema: "app",
                table: "seasons");

            migrationBuilder.DropColumn(
                name: "touched_at",
                schema: "app",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "touched_at",
                schema: "app",
                table: "capture_journal_pieces");
        }
    }
}
