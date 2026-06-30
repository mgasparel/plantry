using Microsoft.EntityFrameworkCore;
using Plantry.SharedKernel;
using Plantry.Shopping.Domain;

namespace Plantry.Shopping.Infrastructure;

/// <summary>
/// EF DbContext for the Shopping bounded context (<c>shopping</c> schema).
/// Owns: <c>shopping_list</c> aggregate root + <c>shopping_list_item</c> children
/// + <c>shopping_list_item_contribution</c> grandchildren (plantry-9scq).
/// Mutable working state (not append-only): items edited in place, hard-deleted on clear (shopping.md).
/// </summary>
public sealed class ShoppingDbContext(DbContextOptions<ShoppingDbContext> options) : DbContext(options)
{
    public DbSet<ShoppingList> ShoppingLists => Set<ShoppingList>();
    public DbSet<ShoppingListItem> ShoppingListItems => Set<ShoppingListItem>();
    public DbSet<ShoppingListItemContribution> ShoppingListItemContributions => Set<ShoppingListItemContribution>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("shopping");

        builder.Entity<ShoppingList>(b =>
        {
            b.ToTable("shopping_list");
            b.HasKey(l => l.Id);
            b.Property(l => l.Id)
                .HasConversion(id => id.Value, v => ShoppingListId.From(v))
                .HasColumnName("shopping_list_id")
                .ValueGeneratedNever();
            b.Property(l => l.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(l => l.Name)
                .HasColumnName("name")
                .IsRequired();
            b.Property(l => l.CreatedAt).HasColumnName("created_at");
            b.Property(l => l.UpdatedAt).HasColumnName("updated_at");

            // Child item collection — backed by _items field (mirrors IntakeDbContext / RecipesDbContext pattern).
            b.HasMany(l => l.Items)
                .WithOne()
                .HasForeignKey(i => i.ShoppingListId)
                .HasPrincipalKey(l => l.Id)
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(l => l.Items)
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasField("_items");

            // Composite UNIQUE so child FK can reference (household_id, shopping_list_id) — per G6-2 convention.
            b.HasIndex(l => new { l.HouseholdId, l.Id })
                .IsUnique()
                .HasDatabaseName("uq_shopping_list_household_list");

            b.HasQueryFilter(l => l.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<ShoppingListItem>(b =>
        {
            b.ToTable("shopping_list_item");
            b.HasKey(i => i.Id);
            b.Property(i => i.Id)
                .HasConversion(id => id.Value, v => ShoppingListItemId.From(v))
                .HasColumnName("shopping_list_item_id")
                .ValueGeneratedNever();
            b.Property(i => i.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(i => i.ShoppingListId)
                .HasConversion(id => id.Value, v => ShoppingListId.From(v))
                .HasColumnName("shopping_list_id")
                .IsRequired();
            b.Property(i => i.ProductId).HasColumnName("product_id");
            b.Property(i => i.FreeText).HasColumnName("free_text");
            // Quantity is derived (SUM of contributions) — not stored on the item row.
            b.Ignore(i => i.Quantity);
            b.Property(i => i.UnitId).HasColumnName("unit_id");
            b.Property(i => i.CategoryId).HasColumnName("category_id");
            b.Property(i => i.Note).HasColumnName("note");
            b.Property(i => i.CheckedAt).HasColumnName("checked_at");
            b.Property(i => i.CheckedBy).HasColumnName("checked_by");
            // source and source_ref have moved to shopping_list_item_contribution (plantry-9scq).
            b.Property(i => i.CreatedAt).HasColumnName("created_at");
            b.Property(i => i.UpdatedAt).HasColumnName("updated_at");

            // Contribution grandchildren — cascade-delete with parent item.
            b.HasMany(i => i.Contributions)
                .WithOne()
                .HasForeignKey(c => c.ItemId)
                .HasPrincipalKey(i => i.Id)
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(i => i.Contributions)
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasField("_contributions");

            // Backing index for the list view (household_id, shopping_list_id) — shopping.md.
            b.HasIndex(i => new { i.HouseholdId, i.ShoppingListId })
                .HasDatabaseName("ix_shopping_list_item_household_list");

            b.HasQueryFilter(i => i.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<ShoppingListItemContribution>(b =>
        {
            b.ToTable("shopping_list_item_contribution");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id)
                .HasConversion(id => id.Value, v => ShoppingListItemContributionId.From(v))
                .HasColumnName("contribution_id")
                .ValueGeneratedNever();
            b.Property(c => c.ItemId)
                .HasConversion(id => id.Value, v => ShoppingListItemId.From(v))
                .HasColumnName("shopping_list_item_id")
                .IsRequired();
            b.Property(c => c.Source)
                .HasConversion(s => s.ToDbValue(), v => ItemSourceExtensions.Parse(v))
                .HasColumnName("source")
                .HasMaxLength(20)
                .IsRequired();
            b.Property(c => c.SourceRef).HasColumnName("source_ref");
            b.Property(c => c.Quantity)
                .HasColumnName("quantity")
                .HasPrecision(12, 3);
            b.Property(c => c.UnitId).HasColumnName("unit_id");

            // Index for per-item contribution lookup.
            b.HasIndex(c => c.ItemId)
                .HasDatabaseName("ix_shopping_list_item_contribution_item");
        });
    }

    // Populated by the RLS middleware before each request (mirrors CatalogDbContext / IntakeDbContext pattern).
    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}
