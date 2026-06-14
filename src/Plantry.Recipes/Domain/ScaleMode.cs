namespace Plantry.Recipes.Domain;

/// <summary>
/// Controls how <see cref="Recipe.ChangeDefaultServings"/> treats stored ingredient quantities
/// (recipes-domain-model.md J7 step 3).
/// </summary>
public enum ScaleMode
{
    /// <summary>Multiply each stored ingredient quantity by new/old ratio.</summary>
    Proportional,

    /// <summary>Leave ingredient quantities unchanged; only update DefaultServings.</summary>
    Keep,
}
