using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContestedZones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contested_zones",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    capture_id = table.Column<Guid>(type: "uuid", nullable: false),
                    league = table.Column<short>(type: "smallint", nullable: false),
                    tile_x = table.Column<int>(type: "integer", nullable: false),
                    tile_y = table.Column<int>(type: "integer", nullable: false),
                    geometry = table.Column<Polygon>(type: "geometry(Polygon, 32634)", nullable: false),
                    contested_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contested_zones", x => x.id);
                    table.ForeignKey(
                        name: "fk_contested_zones_captures_capture_id",
                        column: x => x.capture_id,
                        principalSchema: "app",
                        principalTable: "captures",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_contested_zones_capture_id",
                schema: "app",
                table: "contested_zones",
                column: "capture_id");

            migrationBuilder.CreateIndex(
                name: "ix_contested_zones_league_tile_x_tile_y",
                schema: "app",
                table: "contested_zones",
                columns: new[] { "league", "tile_x", "tile_y" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contested_zones",
                schema: "app");
        }
    }
}
