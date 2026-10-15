using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Duels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "duels",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    challenger_id = table.Column<Guid>(type: "uuid", nullable: true),
                    opponent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    league = table.Column<short>(type: "smallint", nullable: false),
                    metric = table.Column<short>(type: "smallint", nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    days = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    challenger_stake_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    opponent_stake_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    challenger_score = table.Column<double>(type: "double precision", nullable: true),
                    opponent_score = table.Column<double>(type: "double precision", nullable: true),
                    outcome = table.Column<short>(type: "smallint", nullable: true),
                    winner_streak = table.Column<short>(type: "smallint", nullable: true),
                    leader = table.Column<short>(type: "smallint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_duels", x => x.id);
                    table.CheckConstraint("ck_duels_days", "days IN (1, 3, 7)");
                    table.CheckConstraint("ck_duels_enums", "metric BETWEEN 0 AND 3 AND status BETWEEN 0 AND 3 AND (outcome IS NULL OR outcome BETWEEN 0 AND 3) AND (leader IS NULL OR leader BETWEEN 1 AND 2)");
                    table.CheckConstraint("ck_duels_finished", "(status = 2) = (outcome IS NOT NULL)");
                    table.CheckConstraint("ck_duels_not_self", "challenger_id <> opponent_id");
                    table.CheckConstraint("ck_duels_segment", "(metric = 3) = (segment_id IS NOT NULL)");
                    table.CheckConstraint("ck_duels_sides", "status NOT IN (0, 1) OR (challenger_id IS NOT NULL AND opponent_id IS NOT NULL)");
                    table.CheckConstraint("ck_duels_started", "status NOT IN (1, 2) OR (starts_at IS NOT NULL AND ends_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_duels_users_challenger_id",
                        column: x => x.challenger_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_duels_users_opponent_id",
                        column: x => x.opponent_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_duels_challenger_id",
                schema: "app",
                table: "duels",
                column: "challenger_id");

            migrationBuilder.CreateIndex(
                name: "ix_duels_opponent_id",
                schema: "app",
                table: "duels",
                column: "opponent_id");

            migrationBuilder.CreateIndex(
                name: "ix_duels_status_ends_at",
                schema: "app",
                table: "duels",
                columns: new[] { "status", "ends_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "duels",
                schema: "app");
        }
    }
}
