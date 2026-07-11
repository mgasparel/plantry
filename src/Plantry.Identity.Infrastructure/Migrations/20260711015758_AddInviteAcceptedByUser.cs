using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Identity.Infrastructure.Migrations
{
    /// <summary>
    /// plantry-bmfg — hardens household-invite single-use. Two model changes land here, but only ONE is a
    /// real column: <c>accepted_by_user_id</c> (the audit link invite → joining member). The optimistic-
    /// concurrency token is Postgres' <c>xmin</c> <b>system</b> column, which already exists on every row —
    /// it is mapped in the model (see the snapshot) purely so EF composes it into the concurrency-guarded
    /// UPDATE, and adds NO DDL here. The scaffolder emits an <c>AddColumn "xmin"</c> because the model diff
    /// sees a new mapped property; it is deliberately omitted below (an <c>ALTER TABLE ADD COLUMN xmin</c>
    /// would collide with the system column), mirroring how inventory.product_stock maps xmin with no stored
    /// column.
    /// </summary>
    public partial class AddInviteAcceptedByUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "accepted_by_user_id",
                schema: "identity",
                table: "household_invites",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "accepted_by_user_id",
                schema: "identity",
                table: "household_invites");
        }
    }
}
