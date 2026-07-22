using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Pricing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceObservationAmendmentColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-023 A7: nullable self-FK pair backing the supersede-append amendment chain. Every
            // pricing read path filters `superseded_by_id IS NULL` (PriceObservationRepository). A
            // *partial* index on that predicate (`WHERE superseded_by_id IS NULL`) was considered to
            // keep that filter O(1) with no subquery, but is deliberately deferred — there is no
            // profiling data yet showing the plain FK index below (or the existing per-product/per-sku
            // indexes, which already scope the row count per query) is insufficient. Add it later if
            // profiling warrants.
            migrationBuilder.AddColumn<Guid>(
                name: "amends_id",
                schema: "pricing",
                table: "price_observation",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "superseded_by_id",
                schema: "pricing",
                table: "price_observation",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_price_observation_amends_id",
                schema: "pricing",
                table: "price_observation",
                column: "amends_id");

            migrationBuilder.CreateIndex(
                name: "IX_price_observation_superseded_by_id",
                schema: "pricing",
                table: "price_observation",
                column: "superseded_by_id");

            migrationBuilder.AddForeignKey(
                name: "FK_price_observation_price_observation_amends_id",
                schema: "pricing",
                table: "price_observation",
                column: "amends_id",
                principalSchema: "pricing",
                principalTable: "price_observation",
                principalColumn: "observation_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_price_observation_price_observation_superseded_by_id",
                schema: "pricing",
                table: "price_observation",
                column: "superseded_by_id",
                principalSchema: "pricing",
                principalTable: "price_observation",
                principalColumn: "observation_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_price_observation_price_observation_amends_id",
                schema: "pricing",
                table: "price_observation");

            migrationBuilder.DropForeignKey(
                name: "FK_price_observation_price_observation_superseded_by_id",
                schema: "pricing",
                table: "price_observation");

            migrationBuilder.DropIndex(
                name: "IX_price_observation_amends_id",
                schema: "pricing",
                table: "price_observation");

            migrationBuilder.DropIndex(
                name: "IX_price_observation_superseded_by_id",
                schema: "pricing",
                table: "price_observation");

            migrationBuilder.DropColumn(
                name: "amends_id",
                schema: "pricing",
                table: "price_observation");

            migrationBuilder.DropColumn(
                name: "superseded_by_id",
                schema: "pricing",
                table: "price_observation");
        }
    }
}
