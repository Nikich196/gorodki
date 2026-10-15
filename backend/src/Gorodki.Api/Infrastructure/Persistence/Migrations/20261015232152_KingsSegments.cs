using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KingsSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "segments_failures",
                schema: "app",
                table: "runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "segments_processed_at",
                schema: "app",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "segments_retry_at",
                schema: "app",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "segments",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    league = table.Column<short>(type: "smallint", nullable: true),
                    line = table.Column<LineString>(type: "geometry(LineString, 32634)", nullable: false),
                    length_meters = table.Column<double>(type: "double precision", nullable: false),
                    proposal = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_segments", x => x.id);
                    table.CheckConstraint("ck_segments_league", "league IS NULL OR league BETWEEN 1 AND 2");
                    table.CheckConstraint("ck_segments_length", "length_meters BETWEEN 200 AND 800");
                    table.CheckConstraint("ck_segments_line_valid", "extensions.st_isvalid(line)");
                });

            migrationBuilder.CreateTable(
                name: "segment_efforts",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    league = table.Column<short>(type: "smallint", nullable: false),
                    season = table.Column<int>(type: "integer", nullable: true),
                    elapsed_ms = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    visible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_segment_efforts", x => x.id);
                    table.CheckConstraint("ck_segment_efforts_elapsed", "elapsed_ms > 0");
                    table.CheckConstraint("ck_segment_efforts_league", "league BETWEEN 1 AND 2");
                    table.ForeignKey(
                        name: "fk_segment_efforts_runs_run_id",
                        column: x => x.run_id,
                        principalSchema: "app",
                        principalTable: "runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_segment_efforts_segments_segment_id",
                        column: x => x.segment_id,
                        principalSchema: "app",
                        principalTable: "segments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_segment_efforts_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "segment_dethronements",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    league = table.Column<short>(type: "smallint", nullable: false),
                    effort_id = table.Column<long>(type: "bigint", nullable: false),
                    season = table.Column<int>(type: "integer", nullable: true),
                    all_time = table.Column<bool>(type: "boolean", nullable: false),
                    visible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_segment_dethronements", x => x.id);
                    table.CheckConstraint("ck_segment_dethronements_crown", "season IS NOT NULL OR all_time");
                    table.CheckConstraint("ck_segment_dethronements_league", "league BETWEEN 1 AND 2");
                    table.ForeignKey(
                        name: "fk_segment_dethronements_segment_efforts_effort_id",
                        column: x => x.effort_id,
                        principalSchema: "app",
                        principalTable: "segment_efforts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_segment_dethronements_segments_segment_id",
                        column: x => x.segment_id,
                        principalSchema: "app",
                        principalTable: "segments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_segment_dethronements_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "app",
                table: "segments",
                columns: new[] { "id", "league", "length_meters", "line", "name", "proposal" },
                values: new object[,]
                {
                    { new Guid("3d367a82-f861-458c-a588-d1adfc2ea914"), null, 467.80000000000001, (NetTopologySuite.Geometries.LineString)new NetTopologySuite.IO.WKTReader().Read("SRID=32634;LINESTRING (683124 5774846.3, 683168.5 5774854.3, 683216.3 5774870.3, 683238.8 5774883.1, 683247.7 5774865.6, 683304.2 5774899.8, 683316.9 5774881.6, 683512.3 5775018.2)"), "Парк культуры: центральная аллея", true },
                    { new Guid("3efc905d-b526-4f45-abfe-ff17cded566d"), (short)1, 716.39999999999998, (NetTopologySuite.Geometries.LineString)new NetTopologySuite.IO.WKTReader().Read("SRID=32634;LINESTRING (683942.6 5773654.1, 684300.4 5773642.8, 684306 5773645.5, 684331.8 5773644.4, 684337.6 5773641.1, 684440.3 5773637.2, 684474.5 5773638.5, 684521.8 5773634, 684530 5773639.8, 684546.7 5773638.8, 684554.8 5773632.5, 684597.9 5773630.1, 684633.9 5773631.4, 684638.9 5773635, 684651.7 5773635.8)"), "Набережная Мухавца (запад)", true },
                    { new Guid("6a22a3bf-92ee-433f-9f69-1e712cc4681e"), null, 374.10000000000002, (NetTopologySuite.Geometries.LineString)new NetTopologySuite.IO.WKTReader().Read("SRID=32634;LINESTRING (683321.5 5774428.8, 683321.4 5774437.4, 683264.2 5774560.6, 683309 5774581.3, 683311 5774592, 683305.8 5774611.5, 683315.4 5774658, 683257 5774735.9, 683260.5 5774738.5)"), "Парк культуры: южная аллея", true },
                    { new Guid("928be0c7-0e89-435b-b4a1-ae6d710b5e32"), null, 397.30000000000001, (NetTopologySuite.Geometries.LineString)new NetTopologySuite.IO.WKTReader().Read("SRID=32634;LINESTRING (685692.7 5771762.8, 685699.9 5771762.1, 685713.3 5771745.7, 685719 5771746.9, 685745 5771776, 685752.4 5771787.2, 685752.6 5771792.7, 685757.3 5771795.6, 685764.7 5771810.7, 685774.9 5771808.6, 685783.7 5771811.2, 685790.6 5771817.3, 685794.4 5771829.2, 685847.7 5771828, 685880.1 5771832.3, 685885.9 5771836.9, 685892.2 5771833.9, 685938.5 5771843.9, 685940.7 5771855.7, 685950.2 5771864.4, 685963.1 5771865.3, 685970.8 5771861.2, 685975.4 5771855, 686012.5 5771868.5)"), "Парк 1000-летия Бреста", true },
                    { new Guid("e6998253-0408-400f-ac5b-496d39744b0b"), (short)1, 572.70000000000005, (NetTopologySuite.Geometries.LineString)new NetTopologySuite.IO.WKTReader().Read("SRID=32634;LINESTRING (684810.8 5773588.4, 684930.4 5773560.1, 684950 5773559.5, 684964.6 5773565.4, 684977.3 5773576.4, 684985.6 5773590.7, 684991.4 5773617.4, 685000.3 5773634.9, 685010.2 5773643.5, 685027.1 5773651.6, 685253 5773710.1, 685271.7 5773709.4, 685288.8 5773702.3, 685298.9 5773694.2, 685308.5 5773678.1)"), "Набережная Мухавца (восток)", true },
                    { new Guid("eaf3601c-96a9-4578-87fc-7eb0c9823646"), null, 358.89999999999998, (NetTopologySuite.Geometries.LineString)new NetTopologySuite.IO.WKTReader().Read("SRID=32634;LINESTRING (689076.5 5775222.3, 689094.3 5775282.7, 689101.5 5775320.8, 689011.4 5775561.7)"), "Университетская аллея (БрГТУ)", true },
                    { new Guid("fae1d3a6-61ff-426d-8dbb-21c7a36bb527"), (short)2, 499.69999999999999, (NetTopologySuite.Geometries.LineString)new NetTopologySuite.IO.WKTReader().Read("SRID=32634;LINESTRING (684819.4 5773615, 684851.8 5773598.4, 684866.5 5773596.9, 684909 5773616, 684947.7 5773648.2, 685000.2 5773671.5, 685090.3 5773676.4, 685112.2 5773682.3, 685145.4 5773699.5, 685174.4 5773709.6, 685200.7 5773709, 685228.1 5773717.1, 685258.5 5773715.6, 685274.1 5773720.7, 685285 5773717.3)"), "Велодорожка вдоль Мухавца", true }
                });

            migrationBuilder.CreateIndex(
                name: "ix_segment_dethronements_effort_id_user_id",
                schema: "app",
                table: "segment_dethronements",
                columns: new[] { "effort_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_segment_dethronements_segment_id",
                schema: "app",
                table: "segment_dethronements",
                column: "segment_id");

            migrationBuilder.CreateIndex(
                name: "ix_segment_dethronements_user_id_visible_at",
                schema: "app",
                table: "segment_dethronements",
                columns: new[] { "user_id", "visible_at" });

            migrationBuilder.CreateIndex(
                name: "ix_segment_efforts_run_id_segment_id_started_at",
                schema: "app",
                table: "segment_efforts",
                columns: new[] { "run_id", "segment_id", "started_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_segment_efforts_segment_id_league_elapsed_ms",
                schema: "app",
                table: "segment_efforts",
                columns: new[] { "segment_id", "league", "elapsed_ms" });

            migrationBuilder.CreateIndex(
                name: "ix_segment_efforts_user_id",
                schema: "app",
                table: "segment_efforts",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_segments_name",
                schema: "app",
                table: "segments",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "segment_dethronements",
                schema: "app");

            migrationBuilder.DropTable(
                name: "segment_efforts",
                schema: "app");

            migrationBuilder.DropTable(
                name: "segments",
                schema: "app");

            migrationBuilder.DropColumn(
                name: "segments_failures",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "segments_processed_at",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "segments_retry_at",
                schema: "app",
                table: "runs");
        }
    }
}
