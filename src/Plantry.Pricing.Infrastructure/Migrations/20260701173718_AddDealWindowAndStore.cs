using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Pricing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDealWindowAndStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "store_id",
                schema: "pricing",
                table: "price_observation",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "valid_from",
                schema: "pricing",
                table: "price_observation",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "valid_to",
                schema: "pricing",
                table: "price_observation",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_price_observation_deal",
                schema: "pricing",
                table: "price_observation",
                columns: new[] { "household_id", "product_id" },
                filter: "source = 'Deal'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_price_observation_valid_window",
                schema: "pricing",
                table: "price_observation",
                sql: "valid_from <= valid_to");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_price_observation_deal",
                schema: "pricing",
                table: "price_observation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_price_observation_valid_window",
                schema: "pricing",
                table: "price_observation");

            migrationBuilder.DropColumn(
                name: "store_id",
                schema: "pricing",
                table: "price_observation");

            migrationBuilder.DropColumn(
                name: "valid_from",
                schema: "pricing",
                table: "price_observation");

            migrationBuilder.DropColumn(
                name: "valid_to",
                schema: "pricing",
                table: "price_observation");
        }
    }
}
