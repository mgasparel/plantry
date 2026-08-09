using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Market.Infrastructure.Migrations.Market
{
    /// <inheritdoc />
    public partial class AddMatchedDealIdToPriceObservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "matched_deal_id",
                schema: "pricing",
                table: "price_observation",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "matched_deal_id",
                schema: "pricing",
                table: "price_observation");
        }
    }
}
