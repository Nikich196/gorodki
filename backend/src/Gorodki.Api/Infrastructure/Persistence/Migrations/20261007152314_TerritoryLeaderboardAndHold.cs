using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TerritoryLeaderboardAndHold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_score_events_kind",
                schema: "app",
                table: "score_events");

            migrationBuilder.AddColumn<bool>(
                name: "final",
                schema: "app",
                table: "leaderboard_snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "job_runs",
                schema: "app",
                columns: table => new
                {
                    job = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key = table.Column<int>(type: "integer", nullable: false),
                    done_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_runs", x => new { x.job, x.key });
                });

            migrationBuilder.CreateIndex(
                name: "ux_score_events_hold_per_day",
                schema: "app",
                table: "score_events",
                columns: new[] { "user_id", "league", "game_day" },
                unique: true,
                filter: "kind = 3");

            migrationBuilder.AddCheckConstraint(
                name: "ck_score_events_hold",
                schema: "app",
                table: "score_events",
                sql: "kind <> 3 OR (capture_id IS NULL AND run_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_score_events_kind",
                schema: "app",
                table: "score_events",
                sql: "kind BETWEEN 1 AND 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_runs",
                schema: "app");

            migrationBuilder.DropIndex(
                name: "ux_score_events_hold_per_day",
                schema: "app",
                table: "score_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_score_events_hold",
                schema: "app",
                table: "score_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_score_events_kind",
                schema: "app",
                table: "score_events");

            migrationBuilder.DropColumn(
                name: "final",
                schema: "app",
                table: "leaderboard_snapshots");

            migrationBuilder.AddCheckConstraint(
                name: "ck_score_events_kind",
                schema: "app",
                table: "score_events",
                sql: "kind BETWEEN 1 AND 2");
        }
    }
}
