using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

/// <summary>
/// A household-scoped, <b>kind-less</b> vocabulary entry, referenced from recipes by ID
/// (recipes-domain-model.md §5, C2). Its own small aggregate root — not owned by <c>Recipe</c> —
/// because a tag has an independent lifecycle: minted inline from the editor or seeded at household
/// creation, then reused across many recipes. <see cref="Category"/> is cosmetic only; tags carry no
/// hard/soft stance (that is a future Meal-Planning concern).
/// </summary>
public sealed class Tag : AggregateRoot<TagId>
{
    public HouseholdId HouseholdId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public TagCategory? Category { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>True when the tag has been soft-deleted (hidden from the active vocabulary).</summary>
    public bool IsArchived => ArchivedAt.HasValue;

    private Tag() { } // EF

    private Tag(TagId id, HouseholdId householdId, string name, TagCategory? category, DateTimeOffset now)
    {
        Id = id;
        HouseholdId = householdId;
        Name = name;
        Category = category;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public static Tag Create(HouseholdId householdId, string name, TagCategory? category, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Tag(TagId.New(), householdId, name.Trim(), category, clock.UtcNow);
    }

    public void Rename(string name, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        Touch(clock);
    }

    public void SetCategory(TagCategory? category, IClock clock)
    {
        Category = category;
        Touch(clock);
    }

    /// <summary>
    /// Soft-deletes this tag: sets <see cref="ArchivedAt"/> and removes the tag from the active
    /// vocabulary. Archived tags keep their name reserved and still resolve via
    /// <see cref="ITagRepository.ResolveNamesAsync"/> so existing recipe references render.
    /// Idempotent — archiving an already-archived tag is a no-op.
    /// </summary>
    public void Archive(IClock clock)
    {
        if (IsArchived) return;
        ArchivedAt = clock.UtcNow;
        Touch(clock);
    }

    /// <summary>
    /// Restores an archived tag to the active vocabulary.
    /// Idempotent — un-archiving an active tag is a no-op.
    /// </summary>
    public void Unarchive(IClock clock)
    {
        if (!IsArchived) return;
        ArchivedAt = null;
        Touch(clock);
    }

    private void Touch(IClock clock) => UpdatedAt = clock.UtcNow;
}
