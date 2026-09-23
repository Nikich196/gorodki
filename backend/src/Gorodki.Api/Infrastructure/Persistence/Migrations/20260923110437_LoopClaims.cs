using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LoopClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Статусы захватов перенумерованы (Pending = 0). Строк в таблице ещё нет; если вдруг есть —
            // останавливаемся, чтобы не превратить применённые захваты в ожидающие.
            migrationBuilder.Sql(
                """
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM app.captures) THEN
                        RAISE EXCEPTION 'В app.captures есть строки: статусы перенумерованы, нужен перенос данных вручную.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_captures_runs_run_id",
                schema: "app",
                table: "captures");

            migrationBuilder.DropIndex(
                name: "ix_captures_run_id",
                schema: "app",
                table: "captures");

            migrationBuilder.DropIndex(
                name: "ix_captures_user_id_created_at",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "captured_at",
                schema: "app",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "reject_reason",
                schema: "app",
                table: "captures");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "app",
                table: "captures",
                newName: "received_at");

            migrationBuilder.CreateSequence(
                name: "capture_apply_seq",
                schema: "app");

            migrationBuilder.AddColumn<Guid[]>(
                name: "loss_attackers",
                schema: "app",
                table: "parcels",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "loss_window_since",
                schema: "app",
                table: "parcels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "applied_at",
                schema: "app",
                table: "captures",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "applied_seq",
                schema: "app",
                table: "captures",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "area_by_outcome",
                schema: "app",
                table: "captures",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                schema: "app",
                table: "captures",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "changed_tiles",
                schema: "app",
                table: "captures",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "claim_no",
                schema: "app",
                table: "captures",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<short>(
                name: "closure",
                schema: "app",
                table: "captures",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "effective_at",
                schema: "app",
                table: "captures",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "estimated_area",
                schema: "app",
                table: "captures",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "evidence_at",
                schema: "app",
                table: "captures",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                schema: "app",
                table: "captures",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lease_token",
                schema: "app",
                table: "captures",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_until",
                schema: "app",
                table: "captures",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reject_code",
                schema: "app",
                table: "captures",
                type: "character varying(48)",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "territory_config_version",
                schema: "app",
                table: "captures",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_captures_run_id_claim_no",
                schema: "app",
                table: "captures",
                columns: new[] { "run_id", "claim_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_captures_status_lease_until",
                schema: "app",
                table: "captures",
                columns: new[] { "status", "lease_until" },
                filter: "status = 0");

            migrationBuilder.CreateIndex(
                name: "ix_captures_user_id_effective_at",
                schema: "app",
                table: "captures",
                columns: new[] { "user_id", "effective_at" },
                filter: "status = 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_captures_seq",
                schema: "app",
                table: "captures",
                sql: "start_seq >= 0 AND end_seq > start_seq");

            migrationBuilder.AddCheckConstraint(
                name: "ck_captures_status",
                schema: "app",
                table: "captures",
                sql: "status BETWEEN 0 AND 4");

            migrationBuilder.AddForeignKey(
                name: "fk_captures_runs_run_id",
                schema: "app",
                table: "captures",
                column: "run_id",
                principalSchema: "app",
                principalTable: "runs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_captures_runs_run_id",
                schema: "app",
                table: "captures");

            migrationBuilder.DropIndex(
                name: "ix_captures_run_id_claim_no",
                schema: "app",
                table: "captures");

            migrationBuilder.DropIndex(
                name: "ix_captures_status_lease_until",
                schema: "app",
                table: "captures");

            migrationBuilder.DropIndex(
                name: "ix_captures_user_id_effective_at",
                schema: "app",
                table: "captures");

            migrationBuilder.DropCheckConstraint(
                name: "ck_captures_seq",
                schema: "app",
                table: "captures");

            migrationBuilder.DropCheckConstraint(
                name: "ck_captures_status",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "loss_attackers",
                schema: "app",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "loss_window_since",
                schema: "app",
                table: "parcels");

            migrationBuilder.DropColumn(
                name: "applied_at",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "applied_seq",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "area_by_outcome",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "attempts",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "changed_tiles",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "claim_no",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "closure",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "effective_at",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "estimated_area",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "evidence_at",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "last_error",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "lease_token",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "lease_until",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "reject_code",
                schema: "app",
                table: "captures");

            migrationBuilder.DropColumn(
                name: "territory_config_version",
                schema: "app",
                table: "captures");

            migrationBuilder.DropSequence(
                name: "capture_apply_seq",
                schema: "app");

            migrationBuilder.RenameColumn(
                name: "received_at",
                schema: "app",
                table: "captures",
                newName: "created_at");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "captured_at",
                schema: "app",
                table: "parcels",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<short>(
                name: "reject_reason",
                schema: "app",
                table: "captures",
                type: "smallint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_captures_run_id",
                schema: "app",
                table: "captures",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_captures_user_id_created_at",
                schema: "app",
                table: "captures",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_captures_runs_run_id",
                schema: "app",
                table: "captures",
                column: "run_id",
                principalSchema: "app",
                principalTable: "runs",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
