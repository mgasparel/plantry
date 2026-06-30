namespace Plantry.Shopping.Domain;

public readonly record struct ShoppingListId(Guid Value)
{
    public static ShoppingListId New() => new(Guid.CreateVersion7());
    public static ShoppingListId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct ShoppingListItemId(Guid Value)
{
    public static ShoppingListItemId New() => new(Guid.CreateVersion7());
    public static ShoppingListItemId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct ShoppingListItemContributionId(Guid Value)
{
    public static ShoppingListItemContributionId New() => new(Guid.CreateVersion7());
    public static ShoppingListItemContributionId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
