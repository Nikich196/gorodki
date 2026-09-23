using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FogTiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "fog_new_cells",
                schema: "app",
                table: "runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "fog_stamped_at",
                schema: "app",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fog_tiles",
                schema: "app",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layer = table.Column<short>(type: "smallint", nullable: false),
                    season = table.Column<int>(type: "integer", nullable: false),
                    tile_x = table.Column<int>(type: "integer", nullable: false),
                    tile_y = table.Column<int>(type: "integer", nullable: false),
                    bits = table.Column<byte[]>(type: "bytea", nullable: false),
                    cell_count = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fog_tiles", x => new { x.user_id, x.layer, x.season, x.tile_x, x.tile_y });
                    table.CheckConstraint("ck_fog_tiles_cells", "cell_count BETWEEN 1 AND 65536");
                    table.ForeignKey(
                        name: "fk_fog_tiles_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fog_tiles",
                schema: "app");

            migrationBuilder.DropColumn(
                name: "fog_new_cells",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "fog_stamped_at",
                schema: "app",
                table: "runs");
        }
    }
}
