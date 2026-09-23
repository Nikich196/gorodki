using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RunsUpload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:extensions.btree_gist", ",,")
                .Annotation("Npgsql:PostgresExtension:extensions.postgis", ",,")
                .Annotation("Npgsql:PostgresExtension:postgis", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:extensions.postgis", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.AddColumn<string>(
                name: "app_version",
                schema: "app",
                table: "runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "chunk_count",
                schema: "app",
                table: "runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "clock_skew_ms",
                schema: "app",
                table: "runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                schema: "app",
                table: "runs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "device_id",
                schema: "app",
                table: "runs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "last_seq",
                schema: "app",
                table: "runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "motion_authorized",
                schema: "app",
                table: "runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "stored_bytes",
                schema: "app",
                table: "runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ux_runs_one_active_per_user",
                schema: "app",
                table: "runs",
                column: "user_id",
                unique: true,
                filter: "status = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_counters",
                schema: "app",
                table: "runs",
                sql: "chunk_count >= 0 AND stored_bytes >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_runs_last_seq",
                schema: "app",
                table: "runs",
                sql: "last_seq IS NULL OR last_seq >= -1");

            // Куски одного забега не пересекаются по номерам точек: база отвергает пересечение сама (код 23P01),
            // без блокировки строки забега. EF такие ограничения не описывает — поэтому SQL вручную.
            migrationBuilder.Sql(
                """
                ALTER TABLE app.run_chunks ADD CONSTRAINT ex_run_chunks_no_overlap
                EXCLUDE USING gist (run_id WITH =, int4range(first_seq, last_seq, '[]') WITH &&);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE app.run_chunks DROP CONSTRAINT ex_run_chunks_no_overlap;");

            migrationBuilder.DropIndex(
                name: "ux_runs_one_active_per_user",
                schema: "app",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_counters",
                schema: "app",
                table: "runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_runs_last_seq",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "app_version",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "chunk_count",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "clock_skew_ms",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "created_at",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "device_id",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "last_seq",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "motion_authorized",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "stored_bytes",
                schema: "app",
                table: "runs");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:extensions.postgis", ",,")
                .Annotation("Npgsql:PostgresExtension:postgis", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:extensions.btree_gist", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:extensions.postgis", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");
        }
    }
}
