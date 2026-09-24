using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkerRetries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "fog_failures",
                schema: "app",
                table: "runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "fog_retry_at",
                schema: "app",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "visits_failures",
                schema: "app",
                table: "runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "visits_retry_at",
                schema: "app",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_seasons_minsk_midnight",
                schema: "app",
                table: "seasons",
                sql: "(starts_at AT TIME ZONE 'Europe/Minsk')::time = '00:00'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_seasons_minsk_midnight",
                schema: "app",
                table: "seasons");

            migrationBuilder.DropColumn(
                name: "fog_failures",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "fog_retry_at",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "visits_failures",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "visits_retry_at",
                schema: "app",
                table: "runs");
        }
    }
}
