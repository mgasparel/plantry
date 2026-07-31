using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Intake.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowManualImportSourceType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // source_type gains 'Manual' (plantry-45ba.1): a session can now represent a typed
            // purchase, not only a receipt scan. Drop and recreate the closed-set CHECK.
            migrationBuilder.Sql(@"
                ALTER TABLE intake.import_session
                    DROP CONSTRAINT ck_import_session_source_type;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE intake.import_session
                    ADD CONSTRAINT ck_import_session_source_type
                    CHECK (source_type IN ('Receipt', 'Manual'));
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE intake.import_session
                    DROP CONSTRAINT ck_import_session_source_type;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE intake.import_session
                    ADD CONSTRAINT ck_import_session_source_type
                    CHECK (source_type IN ('Receipt'));
            ");
        }
    }
}
