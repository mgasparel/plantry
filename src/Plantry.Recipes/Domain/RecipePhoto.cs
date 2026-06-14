using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

/// <summary>
/// 1:1 with <see cref="Recipe"/> — the image bytes kept off the hot recipe row so Browse/Inspect reads
/// never drag the <c>bytea</c> (recipes-domain-model.md Resolved call 3; mirrors <c>intake.import_receipt</c>).
/// Keyed by the parent recipe id (shared PK + composite FK). <c>SetPhoto</c> upsert /
/// <c>RemovePhoto</c> behaviour is managed by the <see cref="Recipe"/> aggregate root.
/// </summary>
public sealed class RecipePhoto : Entity<RecipeId>
{
    public HouseholdId HouseholdId { get; private set; }
    public byte[] Content { get; internal set; } = [];
    public string ContentType { get; internal set; } = string.Empty;

    /// <summary>Integrity / dedupe hash; nullable (recipes.md).</summary>
    public byte[]? Sha256 { get; internal set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; internal set; }

    private RecipePhoto() { } // EF

    internal static RecipePhoto Create(
        RecipeId recipeId,
        HouseholdId householdId,
        byte[] content,
        string contentType,
        byte[]? sha256,
        DateTimeOffset now)
    {
        return new RecipePhoto
        {
            Id = recipeId,
            HouseholdId = householdId,
            Content = content,
            ContentType = contentType,
            Sha256 = sha256,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
