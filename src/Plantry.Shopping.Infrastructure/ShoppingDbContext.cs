using Microsoft.EntityFrameworkCore;
using Plantry.SharedKernel;
using Plantry.Shopping.Domain;

namespace Plantry.Shopping.Infrastructure;

/// <summary>
/// EF DbContext for the Shopping bounded context (<c>shopping</c> schema).
/// Owns: <c>shopping_list</c> aggregate root + <c>shopping_list_item</c> children.
/// Mutable working state (not append-only): items edited in place, hard-deleted on clear (shopping.md).
/// </summary>
public sealed class ShoppingDbContext(DbContextOptions<ShoppingDbContext> options) : DbContext(options)
{
    public DbSet<ShoppingList> ShoppingLists => Set<ShoppingList>();
    public DbSet<ShoppingListItem> ShoppingListItems => Set<ShoppingListItem>();

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
            b.Property(i => i.Quantity).HasColumnName("quantity").HasPrecision(12, 3);
            b.Property(i => i.UnitId).HasColumnName("unit_id");
            b.Property(i => i.Note).HasColumnName("note");
            b.Property(i => i.CheckedAt).HasColumnName("checked_at");
            b.Property(i => i.CheckedBy).HasColumnName("checked_by");
            b.Property(i => i.Source)
                .HasConversion(s => s.ToDbValue(), v => ItemSourceExtensions.Parse(v))
                .HasColumnName("source")
                .HasMaxLength(20)
                .IsRequired();
            b.Property(i => i.SourceRef).HasColumnName("source_ref");
            b.Property(i => i.CreatedAt).HasColumnName("created_at");
            b.Property(i => i.UpdatedAt).HasColumnName("updated_at");

            // Backing index for the list view (household_id, shopping_list_id) — shopping.md.
            b.HasIndex(i => new { i.HouseholdId, i.ShoppingListId })
                .HasDatabaseName("ix_shopping_list_item_household_list");

            b.HasQueryFilter(i => i.HouseholdId == HouseholdId.From(_householdId));
        });
    }

    // Populated by the RLS middleware before each request (mirrors CatalogDbContext / IntakeDbContext pattern).
    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}
