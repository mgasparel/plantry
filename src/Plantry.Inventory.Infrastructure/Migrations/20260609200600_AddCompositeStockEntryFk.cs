using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositeStockEntryFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Upgrade the stock_journal_entry → stock_entry FK from single-column (entry_id only)
            // to composite (household_id, entry_id), matching the convention that all cross-table
            // FKs carry household_id to make tenancy visible in the schema (G6-2).
            migrationBuilder.Sql(@"
                ALTER TABLE inventory.stock_entry
                    ADD CONSTRAINT uq_stock_entry_household_entry UNIQUE (household_id, entry_id);

                ALTER TABLE inventory.stock_journal_entry
                    DROP CONSTRAINT ""FK_stock_journal_entry_stock_entry_entry_id"";

                ALTER TABLE inventory.stock_journal_entry
                    ADD CONSTRAINT fk_stock_journal_entry_stock_entry
                    FOREIGN KEY (household_id, entry_id)
                    REFERENCES inventory.stock_entry (household_id, entry_id);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE inventory.stock_journal_entry
                    DROP CONSTRAINT IF EXISTS fk_stock_journal_entry_stock_entry;

                ALTER TABLE inventory.stock_journal_entry
                    ADD CONSTRAINT ""FK_stock_journal_entry_stock_entry_entry_id""
                    FOREIGN KEY (entry_id)
                    REFERENCES inventory.stock_entry (entry_id);

                ALTER TABLE inventory.stock_entry
                    DROP CONSTRAINT IF EXISTS uq_stock_entry_household_entry;
            ");
        }
    }
}
