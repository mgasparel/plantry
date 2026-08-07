using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Pantry.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCookReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // plantry-a45c: StockReason gains 'Cook', the addition reason for cook-produced leftovers
            // (previously stamped as 'Purchase', which misrepresented the source in stock history).
            // The check constraint is raw SQL (not part of the EF fluent model), so it is not detected
            // by migration diffing and must be dropped and recreated with the widened allow-list — same
            // shape as AddAmendmentReason.
            migrationBuilder.Sql(@"
                ALTER TABLE inventory.stock_journal_entry
                    DROP CONSTRAINT ck_stock_journal_entry_reason;
                ALTER TABLE inventory.stock_journal_entry
                    ADD CONSTRAINT ck_stock_journal_entry_reason
                    CHECK (reason IN ('Purchase','Consumed','Discarded','Correction','Amendment','Cook'));
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
                    CHECK (reason IN ('Purchase','Consumed','Discarded','Correction','Amendment'));
            ");
        }
    }
}
