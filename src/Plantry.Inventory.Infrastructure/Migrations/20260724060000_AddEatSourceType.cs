using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEatSourceType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // plantry-zcbx: source_type gains 'Eat', stamped by the meal plan's product-dish Eat/Undo
            // consume-and-compensating-add pair. The check constraint is raw SQL (not part of the EF
            // fluent model), so it is not detected by migration diffing and must be dropped and
            // recreated with the widened allow-list — same shape as AddAmendmentReason.
            migrationBuilder.Sql(@"
                ALTER TABLE inventory.stock_journal_entry
                    DROP CONSTRAINT ck_stock_journal_entry_source_type;
                ALTER TABLE inventory.stock_journal_entry
                    ADD CONSTRAINT ck_stock_journal_entry_source_type
                    CHECK (source_type IS NULL OR source_type IN ('Manual','Intake','Cook','Eat'));
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE inventory.stock_journal_entry
                    DROP CONSTRAINT ck_stock_journal_entry_source_type;
                ALTER TABLE inventory.stock_journal_entry
                    ADD CONSTRAINT ck_stock_journal_entry_source_type
                    CHECK (source_type IS NULL OR source_type IN ('Manual','Intake','Cook'));
            ");
        }
    }
}
