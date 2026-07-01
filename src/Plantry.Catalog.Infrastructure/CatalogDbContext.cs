using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel;

namespace Plantry.Catalog.Infrastructure;

/// <summary>
/// EF DbContext for the Catalog bounded context.
/// Owns: units, categories, locations, stores, products (+ SKUs, conversions).
/// </summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : DbContext(options)
{
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("catalog");

        builder.Entity<Unit>(b =>
        {
            b.ToTable("units");
            b.HasKey(u => u.Id);
            b.Property(u => u.Id)
                .HasConversion(id => id.Value, v => UnitId.From(v))
                .HasColumnName("id")
                .ValueGeneratedNever();
            b.Property(u => u.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            // Slice 0 shipped this column as `symbol`; catalog.md calls it `code`. Keep the
            // existing column (already seeded + RLS-tested) and expose it via Unit.Code.
            b.Property(u => u.Code).HasColumnName("symbol").HasMaxLength(20).IsRequired();
            b.Property(u => u.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            b.Property(u => u.Dimension)
                .HasConversion(d => d.ToDbValue(), v => DimensionExtensions.Parse(v))
                .HasColumnName("dimension")
                .HasMaxLength(30)
                .IsRequired();
            b.Property(u => u.FactorToBase).HasColumnName("factor_to_base");
            b.Property(u => u.IsBase).HasColumnName("is_base");

            b.HasIndex(u => new { u.HouseholdId, u.Code }).IsUnique();

            // RLS: filter by household
            b.HasQueryFilter(u => u.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<Category>(b =>
        {
            b.ToTable("categories");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id)
                .HasConversion(id => id.Value, v => CategoryId.From(v))
                .HasColumnName("id")
                .ValueGeneratedNever();
            b.Property(c => c.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(c => c.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            b.Property(c => c.DefaultDueDays).HasColumnName("default_due_days");
            b.Property(c => c.SortOrder).HasColumnName("sort_order");
            b.Property(c => c.Hue).HasColumnName("hue");
            b.Property(c => c.ArchivedAt).HasColumnName("archived_at");

            b.HasIndex(c => new { c.HouseholdId, c.Name }).IsUnique();

            b.HasQueryFilter(c => c.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<Location>(b =>
        {
            b.ToTable("locations");
            b.HasKey(l => l.Id);
            b.Property(l => l.Id)
                .HasConversion(id => id.Value, v => LocationId.From(v))
                .HasColumnName("id")
                .ValueGeneratedNever();
            b.Property(l => l.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(l => l.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            b.Property(l => l.Type)
                .HasConversion(t => t.ToDbValue(), v => LocationTypeExtensions.Parse(v))
                .HasColumnName("location_type")
                .HasMaxLength(20)
                .IsRequired();
            b.Property(l => l.ArchivedAt).HasColumnName("archived_at");

            b.HasIndex(l => new { l.HouseholdId, l.Name }).IsUnique();

            b.HasQueryFilter(l => l.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<Store>(b =>
        {
            b.ToTable("stores");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id)
                .HasConversion(id => id.Value, v => StoreId.From(v))
                .HasColumnName("id")
                .ValueGeneratedNever();
            b.Property(s => s.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(s => s.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            b.Property(s => s.ExternalRef).HasColumnName("external_ref").HasMaxLength(200);
            b.Property(s => s.ArchivedAt).HasColumnName("archived_at");
            b.Property(s => s.CreatedAt).HasColumnName("created_at");
            b.Property(s => s.UpdatedAt).HasColumnName("updated_at");

            b.HasIndex(s => new { s.HouseholdId, s.Name }).IsUnique();
            // Partial unique (catalog.md DM-16 addition): keeps a merchant's external directory id
            // unambiguous so EnsureStore can resolve by it, while manual stores (null external_ref)
            // are excluded from the constraint.
            b.HasIndex(s => new { s.HouseholdId, s.ExternalRef })
                .IsUnique()
                .HasFilter("external_ref IS NOT NULL");

            b.HasQueryFilter(s => s.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<Product>(b =>
        {
            b.ToTable("products");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id)
                .HasConversion(id => id.Value, v => ProductId.From(v))
                .HasColumnName("id")
                .ValueGeneratedNever();
            b.Property(p => p.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(p => p.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            b.Property(p => p.ParentProductId)
                .HasConversion(id => id == null ? (Guid?)null : id.Value.Value, v => v == null ? (ProductId?)null : ProductId.From(v.Value))
                .HasColumnName("parent_product_id");
            b.Property(p => p.CategoryId)
                .HasConversion(id => id == null ? (Guid?)null : id.Value.Value, v => v == null ? (CategoryId?)null : CategoryId.From(v.Value))
                .HasColumnName("category_id");
            b.Property(p => p.DefaultUnitId)
                .HasConversion(id => id.Value, v => UnitId.From(v))
                .HasColumnName("default_unit_id")
                .IsRequired();
            b.Property(p => p.DefaultLocationId)
                .HasConversion(id => id == null ? (Guid?)null : id.Value.Value, v => v == null ? (LocationId?)null : LocationId.From(v.Value))
                .HasColumnName("default_location_id");
            b.Property(p => p.TrackStock).HasColumnName("track_stock");
            b.Property(p => p.DefaultDueDays).HasColumnName("default_due_days");
            b.Property(p => p.DefaultDueDaysAfterOpening).HasColumnName("default_due_days_after_opening");
            b.Property(p => p.DefaultDueDaysAfterFreezing).HasColumnName("default_due_days_after_freezing");
            b.Property(p => p.DefaultDueDaysAfterThawing).HasColumnName("default_due_days_after_thawing");
            b.Property(p => p.HasVariants).HasColumnName("has_variants");
            b.Property(p => p.ArchivedAt).HasColumnName("archived_at");
            b.Property(p => p.CreatedAt).HasColumnName("created_at");
            b.Property(p => p.UpdatedAt).HasColumnName("updated_at");

            b.HasIndex(p => p.HouseholdId);
            b.HasIndex(p => new { p.HouseholdId, p.Name }).IsUnique();

            b.HasMany(p => p.Skus)
                .WithOne()
                .HasForeignKey(s => s.ProductId)
                .HasPrincipalKey(p => p.Id)
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(p => p.Skus).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_skus");

            b.HasMany(p => p.Conversions)
                .WithOne()
                .HasForeignKey(c => c.ProductId)
                .HasPrincipalKey(p => p.Id)
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(p => p.Conversions).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_conversions");

            b.HasQueryFilter(p => p.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<ProductSku>(b =>
        {
            b.ToTable("product_skus");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id)
                .HasConversion(id => id.Value, v => ProductSkuId.From(v))
                .HasColumnName("id")
                .ValueGeneratedNever();
            b.Property(s => s.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(s => s.ProductId)
                .HasConversion(id => id.Value, v => ProductId.From(v))
                .HasColumnName("product_id")
                .IsRequired();
            b.Property(s => s.Label).HasColumnName("label").HasMaxLength(200).IsRequired();
            // Quantity scale per catalog.md persistence conventions: numeric(12,3).
            b.Property(s => s.SizeQuantity).HasColumnName("size_quantity").HasPrecision(12, 3);
            b.Property(s => s.SizeUnitId)
                .HasConversion(id => id == null ? (Guid?)null : id.Value.Value, v => v == null ? (UnitId?)null : UnitId.From(v.Value))
                .HasColumnName("size_unit_id");

            b.HasIndex(s => s.HouseholdId);
            b.HasIndex(s => s.ProductId);

            b.HasQueryFilter(s => s.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<ProductConversion>(b =>
        {
            b.ToTable("product_conversions");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id)
                .HasConversion(id => id.Value, v => ProductConversionId.From(v))
                .HasColumnName("id")
                .ValueGeneratedNever();
            b.Property(c => c.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(c => c.ProductId)
                .HasConversion(id => id.Value, v => ProductId.From(v))
                .HasColumnName("product_id")
                .IsRequired();
            b.Property(c => c.FromUnitId)
                .HasConversion(id => id.Value, v => UnitId.From(v))
                .HasColumnName("from_unit_id")
                .IsRequired();
            b.Property(c => c.ToUnitId)
                .HasConversion(id => id.Value, v => UnitId.From(v))
                .HasColumnName("to_unit_id")
                .IsRequired();
            // Conversion-factor scale per catalog.md persistence conventions: numeric(18,6).
            b.Property(c => c.Factor).HasColumnName("factor").HasPrecision(18, 6).IsRequired();

            b.HasIndex(c => c.HouseholdId);
            b.HasIndex(c => c.ProductId);

            b.HasQueryFilter(c => c.HouseholdId == HouseholdId.From(_householdId));
        });
    }

    // Populated by the RLS middleware before each request
    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}
