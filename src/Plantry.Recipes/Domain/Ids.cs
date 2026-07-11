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

/// <summary>
/// Identity of an <c>Inclusion</c> — a sibling line entity local to the <c>Recipe</c> aggregate (D1).
/// Like <see cref="IngredientId"/> it is addressable only while the recipe is loaded and re-minted on
/// each wholesale save (recipe-composition.md §3, O1).
/// </summary>
public readonly record struct InclusionId(Guid Value)
{
    public static InclusionId New() => new(Guid.CreateVersion7());
    public static InclusionId From(Guid value) => new(value);
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

/// <summary>
/// Identity of a <c>CookProduceLine</c> — a planned yield-on-cook inventory ADD, child of
/// <c>CookEvent</c> (plantry-854a). Like the consume-line id it doubles as the per-cook-unique
/// <c>sourceLineRef</c> idempotency token on the Inventory produce call.
/// </summary>
public readonly record struct CookProduceLineId(Guid Value)
{
    public static CookProduceLineId New() => new(Guid.CreateVersion7());
    public static CookProduceLineId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct TagId(Guid Value)
{
    public static TagId New() => new(Guid.CreateVersion7());
    public static TagId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
