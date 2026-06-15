using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddJournalSourceLineRef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "source_line_ref",
                schema: "inventory",
                table: "stock_journal_entry",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_journal_idempotency",
                schema: "inventory",
                table: "stock_journal_entry",
                columns: new[] { "household_id", "source_ref", "source_line_ref" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_stock_journal_idempotency",
                schema: "inventory",
                table: "stock_journal_entry");

            migrationBuilder.DropColumn(
                name: "source_line_ref",
                schema: "inventory",
                table: "stock_journal_entry");
        }
    }
}
