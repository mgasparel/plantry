using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Deals.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreSubscriptionLastNewContentAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_new_content_at",
                schema: "deals",
                table: "store_subscription",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill (plantry-fsmb, critic pass 1): every row that already exists in a deployed database
            // has last_pulled_at set and last_new_content_at NULL. Without this, each one would render the
            // success-green "Confirmed current" badge with an ever-advancing date on every daily dedup no-op
            // — exactly the defect this ticket removes. The last pull attempt is the only anchor available
            // for pre-existing rows; seeding last_new_content_at from it then freezes that date against
            // later no-ops, same as every post-migration row going forward.
            migrationBuilder.Sql(
                "UPDATE deals.store_subscription SET last_new_content_at = last_pulled_at WHERE last_pulled_at IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_new_content_at",
                schema: "deals",
                table: "store_subscription");
        }
    }
}
