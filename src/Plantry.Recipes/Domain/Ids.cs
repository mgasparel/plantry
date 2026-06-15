namespace Plantry.Recipes.Domain;

public readonly record struct RecipeId(Guid Value)
{
    public static RecipeId New() => new(Guid.CreateVersion7());
    public static RecipeId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Identity of an <c>Ingredient</c> — an entity local to the <c>Recipe</c> aggregate. Addressable
/// only while the recipe is loaded; re-minted on each wholesale save (recipes-domain-model.md O1).
/// </summary>
public readonly record struct IngredientId(Guid Value)
{
    public static IngredientId New() => new(Guid.CreateVersion7());
    public static IngredientId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct CookEventId(Guid Value)
{
    public static CookEventId New() => new(Guid.CreateVersion7());
    public static CookEventId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct CookConsumeLineId(Guid Value)
{
    public static CookConsumeLineId New() => new(Guid.CreateVersion7());
    public static CookConsumeLineId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct TagId(Guid Value)
{
    public static TagId New() => new(Guid.CreateVersion7());
    public static TagId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
