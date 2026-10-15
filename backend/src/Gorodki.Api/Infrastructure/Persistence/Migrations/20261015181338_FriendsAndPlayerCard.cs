using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FriendsAndPlayerCard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "friend_code",
                schema: "app",
                table: "users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "friend_links",
                schema: "app",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    friend_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_friend_links", x => new { x.user_id, x.friend_id });
                    table.CheckConstraint("ck_friend_links_not_self", "user_id <> friend_id");
                    table.ForeignKey(
                        name: "fk_friend_links_users_friend_id",
                        column: x => x.friend_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_friend_links_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_users_friend_code",
                schema: "app",
                table: "users",
                column: "friend_code",
                unique: true,
                filter: "friend_code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_friend_links_friend_id",
                schema: "app",
                table: "friend_links",
                column: "friend_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "friend_links",
                schema: "app");

            migrationBuilder.DropIndex(
                name: "ix_users_friend_code",
                schema: "app",
                table: "users");

            migrationBuilder.DropColumn(
                name: "friend_code",
                schema: "app",
                table: "users");
        }
    }
}
