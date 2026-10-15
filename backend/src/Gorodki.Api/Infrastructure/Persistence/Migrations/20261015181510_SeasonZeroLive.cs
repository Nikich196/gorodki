using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeasonZeroLive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "app",
                table: "seasons",
                keyColumn: "number",
                keyValue: 0,
                columns: new[] { "clean_start", "reset_at", "starts_at" },
                values: new object[] { false, new DateTimeOffset(new DateTime(2026, 10, 14, 21, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2026, 10, 14, 21, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            // Сезон начисления — по времени петли или началу забега (§3.4, docs/architecture/scoring-and-seasons.md). Очки,
            // записанные с 15.10 00:00 по Минску до этой миграции, по прежнему календарю попали «вне сезонов» — по новому
            // это Сезон 0 (до начала Сезона 1). Туман таких забегов остаётся только в слое «за всё время»: разложить его по
            // забегам нельзя (docs/plan-deviations.md, №15, «Риск»).
            migrationBuilder.Sql(
                """
                UPDATE app.score_events SET season = 0
                WHERE season IS NULL AND effective_at >= '2026-10-14 21:00:00+00' AND effective_at < '2026-11-29 21:00:00+00';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // По прежнему календарю всё до 16.11 00:00 по Минску — вне сезонов.
            migrationBuilder.Sql("UPDATE app.score_events SET season = NULL WHERE season = 0 AND effective_at < '2026-11-15 21:00:00+00';");

            migrationBuilder.UpdateData(
                schema: "app",
                table: "seasons",
                keyColumn: "number",
                keyValue: 0,
                columns: new[] { "clean_start", "reset_at", "starts_at" },
                values: new object[] { true, null, new DateTimeOffset(new DateTime(2026, 11, 15, 21, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });
        }
    }
}
