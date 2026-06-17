namespace Plantry.MealPlanning.Domain;

public readonly record struct MealPlanId(Guid Value)
{
    public static MealPlanId New() => new(Guid.CreateVersion7());
    public static MealPlanId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct PlannedMealId(Guid Value)
{
    public static PlannedMealId New() => new(Guid.CreateVersion7());
    public static PlannedMealId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct PlannedDishId(Guid Value)
{
    public static PlannedDishId New() => new(Guid.CreateVersion7());
    public static PlannedDishId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct MealSlotConfigId(Guid Value)
{
    public static MealSlotConfigId New() => new(Guid.CreateVersion7());
    public static MealSlotConfigId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct MealSlotId(Guid Value)
{
    public static MealSlotId New() => new(Guid.CreateVersion7());
    public static MealSlotId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct UserPreferenceId(Guid Value)
{
    public static UserPreferenceId New() => new(Guid.CreateVersion7());
    public static UserPreferenceId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct TagStanceId(Guid Value)
{
    public static TagStanceId New() => new(Guid.CreateVersion7());
    public static TagStanceId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
