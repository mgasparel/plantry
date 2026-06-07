using Microsoft.EntityFrameworkCore;
using Plantry.SharedKernel;

namespace Plantry.Catalog.Infrastructure;

/// <summary>
/// EF DbContext for the Catalog bounded context.
/// Owns: units, categories, locations, products (Slice 1).
/// For Slice 0 only the reference tables (units / categories / locations) are present.
/// </summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : DbContext(options)
{
    public DbSet<UnitRow> Units => Set<UnitRow>();
    public DbSet<CategoryRow> Categories => Set<CategoryRow>();
    public DbSet<LocationRow> Locations => Set<LocationRow>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("catalog");

        builder.Entity<UnitRow>(b =>
        {
            b.ToTable("units");
            b.HasKey(u => u.Id);
            b.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(u => u.HouseholdId).HasColumnName("household_id").IsRequired();
            b.Property(u => u.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            b.Property(u => u.Symbol).HasColumnName("symbol").HasMaxLength(20).IsRequired();
            b.Property(u => u.Dimension).HasColumnName("dimension").HasMaxLength(30).IsRequired();
            b.Property(u => u.FactorToBase).HasColumnName("factor_to_base");
            b.Property(u => u.IsBase).HasColumnName("is_base");

            // RLS: filter by household
            b.HasQueryFilter(u => u.HouseholdId == _householdId);
        });

        builder.Entity<CategoryRow>(b =>
        {
            b.ToTable("categories");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(c => c.HouseholdId).HasColumnName("household_id").IsRequired();
            b.Property(c => c.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            b.Property(c => c.DefaultDueDays).HasColumnName("default_due_days");
            b.Property(c => c.SortOrder).HasColumnName("sort_order");

            b.HasQueryFilter(c => c.HouseholdId == _householdId);
        });

        builder.Entity<LocationRow>(b =>
        {
            b.ToTable("locations");
            b.HasKey(l => l.Id);
            b.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(l => l.HouseholdId).HasColumnName("household_id").IsRequired();
            b.Property(l => l.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            b.Property(l => l.LocationType).HasColumnName("location_type").HasMaxLength(20).IsRequired();

            b.HasQueryFilter(l => l.HouseholdId == _householdId);
        });
    }

    // Populated by the RLS middleware before each request
    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}

// Lightweight read/write row types for Slice 0 (full domain aggregates come in Slice 1)
public sealed class UnitRow
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Dimension { get; set; } = string.Empty;
    public decimal FactorToBase { get; set; } = 1m;
    public bool IsBase { get; set; }
}

public sealed class CategoryRow
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? DefaultDueDays { get; set; }
    public int SortOrder { get; set; }
}

public sealed class LocationRow
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string LocationType { get; set; } = "ambient"; // "ambient" | "frozen"
}
