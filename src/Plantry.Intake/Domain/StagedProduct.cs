using Plantry.SharedKernel;

namespace Plantry.Intake.Domain;

/// <summary>
/// A session-scoped product decision made while reviewing an unmatched receipt line.
///
/// The catalog product is deliberately not created until commit (ADR-010), so this record is the
/// durable identity that multiple receipt lines can share while the review is still in progress.  It
/// is keyed by a generated id, while the normalized name remains unique within the review session. A
/// category or default-unit difference is a conflicting second decision, not a second product identity.
/// </summary>
public sealed class StagedProduct
{
    public const string NormalizedNameUniqueIndexName = "uq_staged_product_household_session_normalized_name";

    private StagedProduct() { } // EF

    public Guid Id { get; private set; }
    public ImportSessionId SessionId { get; private set; }
    public HouseholdId HouseholdId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    /// <summary>Canonical invariant-uppercase key used by the database uniqueness constraint.</summary>
    public string NormalizedName { get; private set; } = string.Empty;
    public Guid? CategoryId { get; private set; }
    public Guid DefaultUnitId { get; private set; }
    public Guid? DefaultLocationId { get; private set; }

    /// <summary>The Catalog product materialized for this staged decision, or null until commit.</summary>
    public Guid? CreatedProductId { get; private set; }

    internal static StagedProduct Create(
        ImportSessionId sessionId,
        HouseholdId householdId,
        string name,
        Guid? categoryId,
        Guid defaultUnitId,
        Guid? defaultLocationId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            SessionId = sessionId,
            HouseholdId = householdId,
            Name = NormalizeName(name),
            NormalizedName = NormalizeNameKey(name),
            CategoryId = categoryId,
            DefaultUnitId = defaultUnitId,
            DefaultLocationId = defaultLocationId,
        };

    /// <summary>
    /// Applies the light name normalization used by the review-session uniqueness rule: trim and collapse
    /// whitespace. Comparison is ordinal case-insensitive so OCR/casing differences cannot create a second
    /// staged identity for the same household receipt review.
    /// </summary>
    internal static string NormalizeName(string name) =>
        string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static string NormalizeNameKey(string name) => NormalizeName(name).ToUpperInvariant();

    public bool HasSameNormalizedName(string name) =>
        string.Equals(NormalizedName, NormalizeNameKey(name), StringComparison.Ordinal);

    /// <summary>Marks the one Catalog product created for this staged decision.</summary>
    public Result MarkMaterialized(Guid productId)
    {
        if (CreatedProductId is { } existing && existing != productId)
            return Error.Custom("Intake.StagedProductAlreadyMaterialized", "This staged product is already linked to another catalog product.");

        CreatedProductId = productId;
        return Result.Success();
    }

    /// <summary>Whether a new-line request carries the exact staged identity represented by this alias.</summary>
    public bool Matches(string name, Guid? categoryId, Guid defaultUnitId, Guid? defaultLocationId) =>
        HasSameNormalizedName(name) &&
        CategoryId == categoryId &&
        DefaultUnitId == defaultUnitId &&
        DefaultLocationId == defaultLocationId;

    /// <summary>Identity check used when reusing an alias for a purchase line.</summary>
    public bool MatchesIdentity(string name, Guid? categoryId, Guid? defaultLocationId) =>
        HasSameNormalizedName(name) &&
        CategoryId == categoryId &&
        DefaultLocationId == defaultLocationId;
}
