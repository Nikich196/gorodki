using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Seasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "seasons",
                schema: "app",
                columns: table => new
                {
                    number = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seasons", x => x.number);
                    table.CheckConstraint("ck_seasons_number", "number >= 0");
                });

            migrationBuilder.InsertData(
                schema: "app",
                table: "seasons",
                columns: new[] { "number", "name", "starts_at" },
                values: new object[,]
                {
                    { 0, "Сезон 0 (бета)", new DateTimeOffset(new DateTime(2026, 11, 15, 21, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { 1, "Сезон 1", new DateTimeOffset(new DateTime(2026, 11, 29, 21, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { 2, "Сезон 2", new DateTimeOffset(new DateTime(2026, 12, 13, 21, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
                });

            // Туман «за всё время» был сезоном 0 — теперь это номер «Сезона 0 (бета)», а «за всё время» — −1.
            migrationBuilder.Sql("UPDATE app.fog_tiles SET season = -1 WHERE season = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE app.fog_tiles SET season = 0 WHERE season = -1;");

            migrationBuilder.DropTable(
                name: "seasons",
                schema: "app");
        }
    }
}
