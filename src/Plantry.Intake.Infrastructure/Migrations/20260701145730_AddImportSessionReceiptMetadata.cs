using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Intake.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddImportSessionReceiptMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "payment_descriptor",
                schema: "intake",
                table: "import_session",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "purchase_date",
                schema: "intake",
                table: "import_session",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "purchase_time",
                schema: "intake",
                table: "import_session",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipt_number",
                schema: "intake",
                table: "import_session",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "store_branch",
                schema: "intake",
                table: "import_session",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "subtotal",
                schema: "intake",
                table: "import_session",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "tax",
                schema: "intake",
                table: "import_session",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "total",
                schema: "intake",
                table: "import_session",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "payment_descriptor",
                schema: "intake",
                table: "import_session");

            migrationBuilder.DropColumn(
                name: "purchase_date",
                schema: "intake",
                table: "import_session");

            migrationBuilder.DropColumn(
                name: "purchase_time",
                schema: "intake",
                table: "import_session");

            migrationBuilder.DropColumn(
                name: "receipt_number",
                schema: "intake",
                table: "import_session");

            migrationBuilder.DropColumn(
                name: "store_branch",
                schema: "intake",
                table: "import_session");

            migrationBuilder.DropColumn(
                name: "subtotal",
                schema: "intake",
                table: "import_session");

            migrationBuilder.DropColumn(
                name: "tax",
                schema: "intake",
                table: "import_session");

            migrationBuilder.DropColumn(
                name: "total",
                schema: "intake",
                table: "import_session");
        }
    }
}
