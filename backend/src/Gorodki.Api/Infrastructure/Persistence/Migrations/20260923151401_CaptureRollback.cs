using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CaptureRollback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "frozen_until",
                schema: "app",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "rollback_id",
                schema: "app",
                table: "captures",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "rolled_back_area",
                schema: "app",
                table: "captures",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rolled_back_at",
                schema: "app",
                table: "captures",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "capture_rollbacks",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rolled_back = table.Column<int>(type: "integer", nullable: false),
                    without_journal = table.Column<int>(type: "integer", nullable: false),
                    failed = table.Column<int>(type: "integer", nullable: false),
                    restored_area = table.Column<double>(type: "double precision", nullable: false),
                    skipped_area = table.Column<double>(type: "double precision", nullable: false),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_capture_rollbacks", x => x.id);
                    table.ForeignKey(
                        name: "fk_capture_rollbacks_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_capture_rollbacks_requested_at",
                schema: "app",
                table: "capture_rollbacks",
                column: "requested_at",
                filter: "status = 0");

            migrationBuilder.CreateIndex(
                name: "ix_capture_rollbacks_user_id",
                schema: "app",
                table: "capture_rollbacks",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "capture_rollbacks",
                schema: "app");

            migrationBuilder.DropColumn(
                name: "frozen_until",
                schema: "app",
                table: "users");

            migrationBuilder.DropColumn(
                name: "rollback_id",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "rolled_back_area",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "rolled_back_at",
                schema: "app",
                table: "captures");
        }
    }
}
