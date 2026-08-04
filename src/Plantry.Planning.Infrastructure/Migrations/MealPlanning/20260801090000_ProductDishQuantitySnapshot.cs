using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Planning.Infrastructure.Migrations;

/// <summary>
/// Converts legacy product dishes (where <c>servings</c> implicitly meant the product default
/// unit) into an explicit quantity/unit snapshot.  Catalog is migrated before MealPlanning by the
/// migrator, so the backfill can join the household-scoped products table without a cross-context FK.
/// </summary>
[Migration("20260801090000_ProductDishQuantitySnapshot")]
public partial class ProductDishQuantitySnapshot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "quantity", schema: "meal_planning", table: "planned_dish",
            type: "numeric(12,3)", nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "unit_id", schema: "meal_planning", table: "planned_dish",
            type: "uuid", nullable: true);
        migrationBuilder.AlterColumn<int>(
            name: "servings", schema: "meal_planning", table: "planned_dish",
            type: "integer", nullable: true, oldClrType: typeof(int), oldType: "integer");

        // Fail loudly before the update if a historical product dish cannot be resolved.  Archived
        // products are intentionally included: plans are historical records and must still migrate.
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM meal_planning.planned_dish d
                    LEFT JOIN catalog.products p
                      ON p.id = d.product_id AND p.household_id = d.household_id
                    WHERE d.product_id IS NOT NULL
                      AND (p.id IS NULL OR p.default_unit_id IS NULL)
                ) THEN
                    RAISE EXCEPTION 'Cannot backfill product dishes: a product is dangling or has no default unit';
                END IF;
            END $$;
            """);

        migrationBuilder.Sql("""
            UPDATE meal_planning.planned_dish d
               SET quantity = d.servings::numeric,
                   unit_id = p.default_unit_id,
                   servings = NULL
              FROM catalog.products p
             WHERE d.product_id IS NOT NULL
               AND p.id = d.product_id
               AND p.household_id = d.household_id;
            """);

        migrationBuilder.Sql("ALTER TABLE meal_planning.planned_dish DROP CONSTRAINT IF EXISTS ck_planned_dish_xor;");
        migrationBuilder.Sql("ALTER TABLE meal_planning.planned_dish DROP CONSTRAINT IF EXISTS ck_planned_dish_servings;");
        migrationBuilder.Sql("""
            ALTER TABLE meal_planning.planned_dish
            ADD CONSTRAINT ck_planned_dish_shape CHECK (
                (recipe_id IS NOT NULL AND product_id IS NULL AND servings IS NOT NULL AND servings >= 1 AND quantity IS NULL AND unit_id IS NULL)
                OR
                (recipe_id IS NULL AND product_id IS NOT NULL AND servings IS NULL AND quantity IS NOT NULL AND quantity > 0 AND unit_id IS NOT NULL AND unit_id <> '00000000-0000-0000-0000-000000000000')
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE meal_planning.planned_dish DROP CONSTRAINT IF EXISTS ck_planned_dish_shape;");
        migrationBuilder.Sql("UPDATE meal_planning.planned_dish SET servings = quantity::integer WHERE product_id IS NOT NULL AND quantity IS NOT NULL;");
        migrationBuilder.AlterColumn<int>(
            name: "servings", schema: "meal_planning", table: "planned_dish",
            type: "integer", nullable: false, oldClrType: typeof(int), oldType: "integer", oldNullable: true);
        migrationBuilder.DropColumn(name: "quantity", schema: "meal_planning", table: "planned_dish");
        migrationBuilder.DropColumn(name: "unit_id", schema: "meal_planning", table: "planned_dish");
        migrationBuilder.Sql("ALTER TABLE meal_planning.planned_dish ADD CONSTRAINT ck_planned_dish_xor CHECK (num_nonnulls(recipe_id, product_id) = 1);");
        migrationBuilder.Sql("ALTER TABLE meal_planning.planned_dish ADD CONSTRAINT ck_planned_dish_servings CHECK (servings >= 1);");
    }
}
