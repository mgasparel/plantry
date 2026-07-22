using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAmendmentReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-023 (purchase-entry-amendment.md): StockReason gains 'Amendment', the compensating
            // fix for a data-entry mistake on a Purchase row. The check constraint is raw SQL (not
            // part of the EF fluent model), so it is not detected by migration diffing and must be
            // dropped and recreated with the widened allow-list — same shape as AddSourceTypeCheckConstraint.
            migrationBuilder.Sql(@"
                ALTER TABLE inventory.stock_journal_entry
                    DROP CONSTRAINT ck_stock_journal_entry_reason;
                ALTER TABLE inventory.stock_journal_entry
                    ADD CONSTRAINT ck_stock_journal_entry_reason
                    CHECK (reason IN ('Purchase','Consumed','Discarded','Correction','Amendment'));
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE inventory.stock_journal_entry
                    DROP CONSTRAINT ck_stock_journal_entry_reason;
                ALTER TABLE inventory.stock_journal_entry
                    ADD CONSTRAINT ck_stock_journal_entry_reason
                    CHECK (reason IN ('Purchase','Consumed','Discarded','Correction'));
            ");
        }
    }
}
