using System.Text.Json.Serialization;

namespace Plantry.Migration.Grocy.Dto;

/// <summary>
/// Raw JSON shapes from the Grocy REST API (/api/objects/*).
/// These are immutable records used only to deserialize the response — no domain logic here.
/// </summary>

public sealed record GrocyProduct(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("product_group_id")] int? ProductGroupId,
    [property: JsonPropertyName("location_id")] int? LocationId,
    [property: JsonPropertyName("qu_id_stock")] int QuIdStock,
    [property: JsonPropertyName("qu_id_purchase")] int QuIdPurchase,
    [property: JsonPropertyName("qu_id_consume")] int? QuIdConsume,
    [property: JsonPropertyName("qu_id_price")] int? QuIdPrice,
    [property: JsonPropertyName("parent_product_id")] int? ParentProductId,
    [property: JsonPropertyName("default_best_before_days")] int? DefaultBestBeforeDays,
    [property: JsonPropertyName("default_best_before_days_after_open")] int? DefaultBestBeforeDaysAfterOpen,
    [property: JsonPropertyName("default_best_before_days_after_freezing")] int? DefaultBestBeforeDaysAfterFreezing,
    [property: JsonPropertyName("default_best_before_days_after_thawing")] int? DefaultBestBeforeDaysAfterThawing,
    [property: JsonPropertyName("min_stock_amount")] decimal? MinStockAmount,
    [property: JsonPropertyName("calories")] decimal? Calories,
    [property: JsonPropertyName("shopping_location_id")] int? ShoppingLocationId,
    [property: JsonPropertyName("picture_file_name")] string? PictureFileName,
    [property: JsonPropertyName("not_check_stock_fulfillment_for_recipes")] int? NotCheckStockFulfillmentForRecipes,
    [property: JsonPropertyName("row_created_timestamp")] string? RowCreatedTimestamp
);

public sealed record GrocyQuantityUnit(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("row_created_timestamp")] string? RowCreatedTimestamp
);

public sealed record GrocyQuantityUnitConversion(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("from_qu_id")] int FromQuId,
    [property: JsonPropertyName("to_qu_id")] int ToQuId,
    [property: JsonPropertyName("factor")] decimal Factor,
    [property: JsonPropertyName("product_id")] int? ProductId,
    [property: JsonPropertyName("row_created_timestamp")] string? RowCreatedTimestamp
);

public sealed record GrocyLocation(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("is_freezer")] int IsFreezer,
    [property: JsonPropertyName("row_created_timestamp")] string? RowCreatedTimestamp
);

public sealed record GrocyProductGroup(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("row_created_timestamp")] string? RowCreatedTimestamp
);

public sealed record GrocyRecipe(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("base_servings")] int BaseServings,
    [property: JsonPropertyName("desired_servings")] int? DesiredServings,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("product_id")] int? ProductId,
    [property: JsonPropertyName("picture_file_name")] string? PictureFileName,
    [property: JsonPropertyName("row_created_timestamp")] string? RowCreatedTimestamp
);

public sealed record GrocyRecipePosition(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("recipe_id")] int RecipeId,
    [property: JsonPropertyName("product_id")] int ProductId,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("qu_id")] int QuId,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("ingredient_group")] string? IngredientGroup,
    [property: JsonPropertyName("not_check_stock_fulfillment")] int? NotCheckStockFulfillment,
    [property: JsonPropertyName("only_check_single_unit_in_stock")] int? OnlyCheckSingleUnitInStock,
    [property: JsonPropertyName("variable_amount")] string? VariableAmount,
    [property: JsonPropertyName("price_factor")] decimal? PriceFactor,
    [property: JsonPropertyName("row_created_timestamp")] string? RowCreatedTimestamp
);

public sealed record GrocyRecipeNesting(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("recipe_id")] int RecipeId,
    [property: JsonPropertyName("includes_recipe_id")] int IncludesRecipeId,
    [property: JsonPropertyName("servings")] decimal Servings,
    [property: JsonPropertyName("row_created_timestamp")] string? RowCreatedTimestamp
);

public sealed record GrocyUserfield(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("entity")] string Entity,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("caption")] string? Caption,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("show_as_column_in_tables")] int? ShowAsColumnInTables,
    [property: JsonPropertyName("row_created_timestamp")] string? RowCreatedTimestamp
);

public sealed record GrocyProductBarcode(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("product_id")] int ProductId,
    [property: JsonPropertyName("barcode")] string Barcode,
    [property: JsonPropertyName("qu_id")] int? QuId,
    [property: JsonPropertyName("amount")] decimal? Amount,
    [property: JsonPropertyName("shopping_location_id")] int? ShoppingLocationId,
    [property: JsonPropertyName("row_created_timestamp")] string? RowCreatedTimestamp
);
