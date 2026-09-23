using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LeaderboardSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "leaderboard_snapshots",
                schema: "app",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    board = table.Column<short>(type: "smallint", nullable: false),
                    layer = table.Column<short>(type: "smallint", nullable: false),
                    season = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_leaderboard_snapshots", x => new { x.day, x.board, x.layer, x.season, x.user_id });
                    table.ForeignKey(
                        name: "fk_leaderboard_snapshots_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_leaderboard_snapshots_day_board_layer_season_rank",
                schema: "app",
                table: "leaderboard_snapshots",
                columns: new[] { "day", "board", "layer", "season", "rank" });

            migrationBuilder.CreateIndex(
                name: "ix_leaderboard_snapshots_user_id",
                schema: "app",
                table: "leaderboard_snapshots",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "leaderboard_snapshots",
                schema: "app");
        }
    }
}
