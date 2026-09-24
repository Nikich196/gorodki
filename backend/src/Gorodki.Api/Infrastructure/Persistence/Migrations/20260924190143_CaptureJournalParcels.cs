using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CaptureJournalParcels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "capture_journal_parcels",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    capture_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tile_x = table.Column<int>(type: "integer", nullable: false),
                    tile_y = table.Column<int>(type: "integer", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    replaced = table.Column<bool>(type: "boolean", nullable: false),
                    parcel_id = table.Column<long>(type: "bigint", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    level = table.Column<short>(type: "smallint", nullable: false),
                    last_visit_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_level_up_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    shield_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    siege_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    loss_window_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    loss_attackers = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    geometry = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_capture_journal_parcels", x => x.id);
                    table.CheckConstraint("ck_capture_journal_parcels_geometry", "replaced = (geometry IS NOT NULL)");
                    table.CheckConstraint("ck_capture_journal_parcels_level", "level BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "fk_capture_journal_parcels_capture_journal_capture_id_tile_x_t",
                        columns: x => new { x.capture_id, x.tile_x, x.tile_y },
                        principalSchema: "app",
                        principalTable: "capture_journal",
                        principalColumns: new[] { "capture_id", "tile_x", "tile_y" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_capture_journal_parcels_applied_at",
                schema: "app",
                table: "capture_journal_parcels",
                column: "applied_at");

            migrationBuilder.CreateIndex(
                name: "ix_capture_journal_parcels_capture_id_tile_x_tile_y_replaced_p",
                schema: "app",
                table: "capture_journal_parcels",
                columns: new[] { "capture_id", "tile_x", "tile_y", "replaced", "parcel_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "capture_journal_parcels",
                schema: "app");
        }
    }
}
