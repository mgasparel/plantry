using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

/// <summary>
/// 1:1 with <see cref="Recipe"/> — the image bytes kept off the hot recipe row so Browse/Inspect reads
/// never drag the <c>bytea</c> (recipes-domain-model.md Resolved call 3; mirrors <c>intake.import_receipt</c>).
/// Keyed by the parent recipe id (shared PK + composite FK). P2-0 step maps the shape only; <c>SetPhoto</c>
/// upsert / <c>RemovePhoto</c> behaviour lands in P2-1.
/// </summary>
public sealed class RecipePhoto : Entity<RecipeId>
{
    public HouseholdId HouseholdId { get; private set; }
    public byte[] Content { get; private set; } = [];
    public string ContentType { get; private set; } = string.Empty;

    /// <summary>Integrity / dedupe hash; nullable (recipes.md).</summary>
    public byte[]? Sha256 { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private RecipePhoto() { } // EF
}
