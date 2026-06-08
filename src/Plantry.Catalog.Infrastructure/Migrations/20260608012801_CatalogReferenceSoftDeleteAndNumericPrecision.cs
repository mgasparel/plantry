using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CatalogReferenceSoftDeleteAndNumericPrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "size_quantity",
                schema: "catalog",
                table: "product_skus",
                type: "numeric(12,3)",
                precision: 12,
                scale: 3,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "factor",
                schema: "catalog",
                table: "product_conversions",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "archived_at",
                schema: "catalog",
                table: "locations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "archived_at",
                schema: "catalog",
                table: "categories",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "archived_at",
                schema: "catalog",
                table: "locations");

            migrationBuilder.DropColumn(
                name: "archived_at",
                schema: "catalog",
                table: "categories");

            migrationBuilder.AlterColumn<decimal>(
                name: "size_quantity",
                schema: "catalog",
                table: "product_skus",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,3)",
                oldPrecision: 12,
                oldScale: 3,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "factor",
                schema: "catalog",
                table: "product_conversions",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)",
                oldPrecision: 18,
                oldScale: 6);
        }
    }
}
