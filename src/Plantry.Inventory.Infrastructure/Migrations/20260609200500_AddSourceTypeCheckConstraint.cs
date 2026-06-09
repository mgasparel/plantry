using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceTypeCheckConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // source_type is a closed enum (Manual/Intake/Cook) persisted as text — enforce it at the DB
            // level to match the convention applied to the reason column (ADR-011 / DM-14).
            migrationBuilder.Sql(@"
                ALTER TABLE inventory.stock_journal_entry
                    ADD CONSTRAINT ck_stock_journal_entry_source_type
                    CHECK (source_type IS NULL OR source_type IN ('Manual','Intake','Cook'));
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE inventory.stock_journal_entry
                    DROP CONSTRAINT IF EXISTS ck_stock_journal_entry_source_type;
            ");
        }
    }
}
