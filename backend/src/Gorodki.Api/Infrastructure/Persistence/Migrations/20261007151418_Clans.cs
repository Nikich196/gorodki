using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Clans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "clan_join_after",
                schema: "app",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "clans",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    hue = table.Column<short>(type: "smallint", nullable: false),
                    invite_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    renamed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clans", x => x.id);
                    table.CheckConstraint("ck_clans_hue", "hue BETWEEN 0 AND 11");
                });

            migrationBuilder.CreateTable(
                name: "clan_members",
                schema: "app",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    clan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<short>(type: "smallint", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clan_members", x => x.user_id);
                    table.CheckConstraint("ck_clan_members_role", "role BETWEEN 0 AND 2");
                    table.ForeignKey(
                        name: "fk_clan_members_clans_clan_id",
                        column: x => x.clan_id,
                        principalSchema: "app",
                        principalTable: "clans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_clan_members_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clan_members_clan_id",
                schema: "app",
                table: "clan_members",
                column: "clan_id");

            migrationBuilder.CreateIndex(
                name: "ux_clan_members_one_leader",
                schema: "app",
                table: "clan_members",
                column: "clan_id",
                unique: true,
                filter: "role = 2");

            migrationBuilder.CreateIndex(
                name: "ix_clans_invite_code",
                schema: "app",
                table: "clans",
                column: "invite_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clans_normalized_name",
                schema: "app",
                table: "clans",
                column: "normalized_name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clan_members",
                schema: "app");

            migrationBuilder.DropTable(
                name: "clans",
                schema: "app");

            migrationBuilder.DropColumn(
                name: "clan_join_after",
                schema: "app",
                table: "users");
        }
    }
}
