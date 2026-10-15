using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Карточка недели (PLAN.md, §3.15; #141): у забега — площадь новых клеток тумана и «новые достижимые» клетки набора OSM
    /// (прирост «% Бреста»). Забегам, уже открывшим туман, площадь — оценкой: клетка G22 на широте центра Бреста (52,0976°)
    /// ≈ 34,45 м² (по городу площадь клетки меняется меньше чем на 0,5 %); «достижимых» у них нет — набора тогда не было.
    /// </summary>
    public partial class WeeklyCard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "fog_new_reachable_cells",
                schema: "app",
                table: "runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "fog_new_square_meters",
                schema: "app",
                table: "runs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "fog_osm_set",
                schema: "app",
                table: "runs",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE app.runs SET fog_new_square_meters = round((fog_new_cells * 34.452)::numeric, 1) WHERE fog_new_cells IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "fog_new_reachable_cells",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "fog_new_square_meters",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "fog_osm_set",
                schema: "app",
                table: "runs");
        }
    }
}
