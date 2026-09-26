using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OsmPipelineSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "osm_sets",
                schema: "app",
                columns: table => new
                {
                    version = table.Column<int>(type: "integer", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: false),
                    frame_min_x = table.Column<int>(type: "integer", nullable: false),
                    frame_min_y = table.Column<int>(type: "integer", nullable: false),
                    frame_max_x = table.Column<int>(type: "integer", nullable: false),
                    frame_max_y = table.Column<int>(type: "integer", nullable: false),
                    play_zone = table.Column<short>(type: "smallint", nullable: false),
                    reachable_cells = table.Column<int>(type: "integer", nullable: false),
                    built_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_osm_sets", x => x.version);
                    table.CheckConstraint("ck_osm_sets_frame", "frame_min_x <= frame_max_x AND frame_min_y <= frame_max_y");
                    table.CheckConstraint("ck_osm_sets_play_zone", "play_zone BETWEEN 0 AND 2");
                    table.CheckConstraint("ck_osm_sets_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "districts",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    set_version = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    osm_id = table.Column<long>(type: "bigint", nullable: true),
                    proposal = table.Column<bool>(type: "boolean", nullable: false),
                    geometry = table.Column<MultiPolygon>(type: "geometry(MultiPolygon, 32634)", nullable: false),
                    area_without_masks = table.Column<double>(type: "double precision", nullable: false),
                    reachable_cells = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_districts", x => x.id);
                    table.CheckConstraint("ck_districts_geometry_valid", "extensions.st_isvalid(geometry)");
                    table.CheckConstraint("ck_districts_kind", "kind BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "fk_districts_osm_sets_set_version",
                        column: x => x.set_version,
                        principalSchema: "app",
                        principalTable: "osm_sets",
                        principalColumn: "version",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "land_zones",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    set_version = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    tile_x = table.Column<int>(type: "integer", nullable: false),
                    tile_y = table.Column<int>(type: "integer", nullable: false),
                    geometry = table.Column<Polygon>(type: "geometry(Polygon, 32634)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_land_zones", x => x.id);
                    table.CheckConstraint("ck_land_zones_geometry_valid", "extensions.st_isvalid(geometry)");
                    table.CheckConstraint("ck_land_zones_kind", "kind = 1");
                    table.ForeignKey(
                        name: "fk_land_zones_osm_sets_set_version",
                        column: x => x.set_version,
                        principalSchema: "app",
                        principalTable: "osm_sets",
                        principalColumn: "version",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "masks",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    set_version = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    tile_x = table.Column<int>(type: "integer", nullable: false),
                    tile_y = table.Column<int>(type: "integer", nullable: false),
                    geometry = table.Column<Polygon>(type: "geometry(Polygon, 32634)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_masks", x => x.id);
                    table.CheckConstraint("ck_masks_geometry_valid", "extensions.st_isvalid(geometry)");
                    table.CheckConstraint("ck_masks_kind", "kind BETWEEN 1 AND 8");
                    table.ForeignKey(
                        name: "fk_masks_osm_sets_set_version",
                        column: x => x.set_version,
                        principalSchema: "app",
                        principalTable: "osm_sets",
                        principalColumn: "version",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reachable_tiles",
                schema: "app",
                columns: table => new
                {
                    set_version = table.Column<int>(type: "integer", nullable: false),
                    tile_x = table.Column<int>(type: "integer", nullable: false),
                    tile_y = table.Column<int>(type: "integer", nullable: false),
                    bits = table.Column<byte[]>(type: "bytea", nullable: false),
                    cell_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reachable_tiles", x => new { x.set_version, x.tile_x, x.tile_y });
                    table.CheckConstraint("ck_reachable_tiles_cells", "cell_count BETWEEN 1 AND 65536");
                    table.ForeignKey(
                        name: "fk_reachable_tiles_osm_sets_set_version",
                        column: x => x.set_version,
                        principalSchema: "app",
                        principalTable: "osm_sets",
                        principalColumn: "version",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "district_tiles",
                schema: "app",
                columns: table => new
                {
                    district_id = table.Column<long>(type: "bigint", nullable: false),
                    tile_x = table.Column<int>(type: "integer", nullable: false),
                    tile_y = table.Column<int>(type: "integer", nullable: false),
                    bits = table.Column<byte[]>(type: "bytea", nullable: false),
                    cell_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_district_tiles", x => new { x.district_id, x.tile_x, x.tile_y });
                    table.CheckConstraint("ck_district_tiles_cells", "cell_count BETWEEN 1 AND 65536");
                    table.ForeignKey(
                        name: "fk_district_tiles_districts_district_id",
                        column: x => x.district_id,
                        principalSchema: "app",
                        principalTable: "districts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_districts_set_version_key",
                schema: "app",
                table: "districts",
                columns: new[] { "set_version", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_land_zones_set_version_tile_x_tile_y",
                schema: "app",
                table: "land_zones",
                columns: new[] { "set_version", "tile_x", "tile_y" });

            migrationBuilder.CreateIndex(
                name: "ix_masks_set_version_tile_x_tile_y",
                schema: "app",
                table: "masks",
                columns: new[] { "set_version", "tile_x", "tile_y" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "district_tiles",
                schema: "app");

            migrationBuilder.DropTable(
                name: "land_zones",
                schema: "app");

            migrationBuilder.DropTable(
                name: "masks",
                schema: "app");

            migrationBuilder.DropTable(
                name: "reachable_tiles",
                schema: "app");

            migrationBuilder.DropTable(
                name: "districts",
                schema: "app");

            migrationBuilder.DropTable(
                name: "osm_sets",
                schema: "app");
        }
    }
}
