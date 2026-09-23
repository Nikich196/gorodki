using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MultiAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "accepted_meters",
                schema: "app",
                table: "runs",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_runs_device_id",
                schema: "app",
                table: "runs",
                column: "device_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_runs_device_id",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "accepted_meters",
                schema: "app",
                table: "runs");
        }
    }
}
