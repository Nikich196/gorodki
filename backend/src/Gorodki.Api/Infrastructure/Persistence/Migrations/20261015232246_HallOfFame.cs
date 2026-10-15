using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Зал славы (PLAN.md, §3.4; #137): снимок итога закрытого сезона — места 1–3 игроков и первое место клана по лиге.
    /// Удаление аккаунта обезличивает строку (<c>user_id</c> → <c>null</c>), распад клана — <c>clan_id</c> → <c>null</c>.
    /// </summary>
    public partial class HallOfFame : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hall_of_fame",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    season = table.Column<int>(type: "integer", nullable: false),
                    league = table.Column<short>(type: "smallint", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    place = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    clan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    clan_name = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    value = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hall_of_fame", x => x.id);
                    table.CheckConstraint("ck_hall_of_fame_kind", "(kind = 0 AND clan_id IS NULL AND clan_name IS NULL) OR (kind = 1 AND user_id IS NULL AND place = 1 AND clan_name IS NOT NULL)");
                    table.CheckConstraint("ck_hall_of_fame_place", "place BETWEEN 1 AND 3 AND value >= 0");
                    table.ForeignKey(
                        name: "fk_hall_of_fame_clans_clan_id",
                        column: x => x.clan_id,
                        principalSchema: "app",
                        principalTable: "clans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_hall_of_fame_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hall_of_fame_clan_id",
                schema: "app",
                table: "hall_of_fame",
                column: "clan_id");

            migrationBuilder.CreateIndex(
                name: "ix_hall_of_fame_season",
                schema: "app",
                table: "hall_of_fame",
                column: "season");

            migrationBuilder.CreateIndex(
                name: "ix_hall_of_fame_user_id",
                schema: "app",
                table: "hall_of_fame",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hall_of_fame",
                schema: "app");
        }
    }
}
