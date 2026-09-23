using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RunReadiness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "newcomer",
                schema: "app",
                table: "runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // −1 — «точки 0 ещё нет»: ноль означал бы, что начало следа уже пришло.
            migrationBuilder.AddColumn<int>(
                name: "prefix_end_seq",
                schema: "app",
                table: "runs",
                type: "integer",
                nullable: false,
                defaultValue: -1);

            migrationBuilder.AddColumn<long>(
                name: "prefix_sensors_ms",
                schema: "app",
                table: "runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "first_point_ms",
                schema: "app",
                table: "run_chunks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "last_point_ms",
                schema: "app",
                table: "run_chunks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "sensors_complete_through_ms",
                schema: "app",
                table: "run_chunks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "newcomer",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "prefix_end_seq",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "prefix_sensors_ms",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "first_point_ms",
                schema: "app",
                table: "run_chunks");

            migrationBuilder.DropColumn(
                name: "last_point_ms",
                schema: "app",
                table: "run_chunks");

            migrationBuilder.DropColumn(
                name: "sensors_complete_through_ms",
                schema: "app",
                table: "run_chunks");
        }
    }
}
