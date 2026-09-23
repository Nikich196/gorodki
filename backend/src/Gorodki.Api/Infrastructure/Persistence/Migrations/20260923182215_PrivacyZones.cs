using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrivacyZones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "privacy_zones",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: false),
                    longitude = table.Column<double>(type: "double precision", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_privacy_zones", x => x.id);
                    table.CheckConstraint("ck_privacy_zones_coordinates", "latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180");
                    table.ForeignKey(
                        name: "fk_privacy_zones_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_privacy_zones_user_id",
                schema: "app",
                table: "privacy_zones",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "privacy_zones",
                schema: "app");
        }
    }
}
