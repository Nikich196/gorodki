using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Feed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feed_posts",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    league = table.Column<short>(type: "smallint", nullable: false),
                    game_day = table.Column<DateOnly>(type: "date", nullable: false),
                    distance_meters = table.Column<double>(type: "double precision", nullable: true),
                    captured_square_meters = table.Column<double>(type: "double precision", nullable: true),
                    capture_id = table.Column<Guid>(type: "uuid", nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    visible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feed_posts", x => x.id);
                    table.CheckConstraint("ck_feed_posts_numbers", "(kind = 0 AND captured_square_meters IS NOT NULL AND distance_meters IS NULL) OR (kind = 1 AND distance_meters IS NOT NULL AND captured_square_meters IS NULL)");
                    table.ForeignKey(
                        name: "fk_feed_posts_captures_capture_id",
                        column: x => x.capture_id,
                        principalSchema: "app",
                        principalTable: "captures",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_feed_posts_runs_run_id",
                        column: x => x.run_id,
                        principalSchema: "app",
                        principalTable: "runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_feed_posts_users_author_id",
                        column: x => x.author_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "player_blocks",
                schema: "app",
                columns: table => new
                {
                    blocker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocked_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_player_blocks", x => new { x.blocker_id, x.blocked_id });
                    table.CheckConstraint("ck_player_blocks_not_self", "blocker_id <> blocked_id");
                    table.ForeignKey(
                        name: "fk_player_blocks_users_blocked_id",
                        column: x => x.blocked_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_player_blocks_users_blocker_id",
                        column: x => x.blocker_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "feed_reports",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feed_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_feed_reports_feed_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "app",
                        principalTable: "feed_posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_feed_reports_users_reporter_id",
                        column: x => x.reporter_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "feed_respects",
                schema: "app",
                columns: table => new
                {
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feed_respects", x => new { x.post_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_feed_respects_feed_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "app",
                        principalTable: "feed_posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_feed_respects_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_feed_posts_author_id",
                schema: "app",
                table: "feed_posts",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_feed_posts_capture_id",
                schema: "app",
                table: "feed_posts",
                column: "capture_id",
                unique: true,
                filter: "capture_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_feed_posts_visible_at_id",
                schema: "app",
                table: "feed_posts",
                columns: new[] { "visible_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_feed_posts_run_post",
                schema: "app",
                table: "feed_posts",
                column: "run_id",
                unique: true,
                filter: "kind = 1");

            migrationBuilder.CreateIndex(
                name: "ix_feed_reports_post_id_reporter_id",
                schema: "app",
                table: "feed_reports",
                columns: new[] { "post_id", "reporter_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_feed_reports_reporter_id",
                schema: "app",
                table: "feed_reports",
                column: "reporter_id");

            migrationBuilder.CreateIndex(
                name: "ix_feed_respects_user_id",
                schema: "app",
                table: "feed_respects",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_player_blocks_blocked_id",
                schema: "app",
                table: "player_blocks",
                column: "blocked_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feed_reports",
                schema: "app");

            migrationBuilder.DropTable(
                name: "feed_respects",
                schema: "app");

            migrationBuilder.DropTable(
                name: "player_blocks",
                schema: "app");

            migrationBuilder.DropTable(
                name: "feed_posts",
                schema: "app");
        }
    }
}
