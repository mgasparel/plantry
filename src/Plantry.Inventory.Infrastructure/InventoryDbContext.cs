using Microsoft.EntityFrameworkCore;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;

namespace Plantry.Inventory.Infrastructure;

/// <summary>
/// EF DbContext for the Inventory bounded context.
/// Owns: product_stock (aggregate root) + its stock_entry lots + the append-only stock_journal_entry.
/// </summary>
public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options)
    : DbContext(options)
{
    public DbSet<ProductStock> ProductStocks => Set<ProductStock>();
    public DbSet<StockEntry> StockEntries => Set<StockEntry>();
    public DbSet<StockJournalEntry> StockJournalEntries => Set<StockJournalEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("inventory");

        builder.Entity<ProductStock>(b =>
        {
            b.ToTable("product_stock");

            // Composite PK (household_id, product_id) — the ADR-010 keying. The base Entity.Id
            // (a ProductStockId value pair) is not a stored column; identity lives in these two.
            b.Ignore(p => p.Id);
            b.Property(p => p.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(p => p.ProductId).HasColumnName("product_id").IsRequired();
            b.HasKey(p => new { p.HouseholdId, p.ProductId });

            b.Property(p => p.CreatedAt).HasColumnName("created_at");
            b.Property(p => p.UpdatedAt).HasColumnName("updated_at");

            // Optimistic-concurrency backstop: Postgres' xmin system column, no stored column and
            // no app-side increment (inventory.md resolved-call #1). Npgsql maps a uint shadow
            // property named "xmin" to the system column. The authoritative serialization is the
            // repository's SELECT … FOR UPDATE on this root row.
            b.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

            b.HasMany(p => p.Entries)
                .WithOne()
                .HasForeignKey(e => new { e.HouseholdId, e.ProductId })
                .HasPrincipalKey(p => new { p.HouseholdId, p.ProductId })
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(p => p.Entries).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_entries");

            // The journal is scoped to the aggregate by (household_id, product_id) so it persists in
            // the same unit of work; its entry_id FK to stock_entry is configured on the journal below.
            b.HasMany(p => p.Journal)
                .WithOne()
                .HasForeignKey(j => new { j.HouseholdId, j.ProductId })
                .HasPrincipalKey(p => new { p.HouseholdId, p.ProductId })
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(p => p.Journal).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_journal");

            b.HasQueryFilter(p => p.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<StockEntry>(b =>
        {
            b.ToTable("stock_entry");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id)
                .HasConversion(id => id.Value, v => StockEntryId.From(v))
                .HasColumnName("entry_id")
                .ValueGeneratedNever();
            b.Property(e => e.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(e => e.ProductId).HasColumnName("product_id").IsRequired();
            b.Property(e => e.SkuId).HasColumnName("sku_id");
            b.Property(e => e.Quantity).HasColumnName("quantity").HasPrecision(12, 3);
            b.Property(e => e.UnitId).HasColumnName("unit_id").IsRequired();
            b.Property(e => e.LocationId).HasColumnName("location_id").IsRequired();
            b.Property(e => e.ExpiryDate).HasColumnName("expiry_date");
            b.Property(e => e.IsOpen).HasColumnName("is_open");
            b.Property(e => e.FrozenAt).HasColumnName("frozen_at");
            b.Property(e => e.ThawedAt).HasColumnName("thawed_at");
            b.Property(e => e.PurchasedAt).HasColumnName("purchased_at");
            b.Property(e => e.DepletedAt).HasColumnName("depleted_at");
            b.Property(e => e.CreatedAt).HasColumnName("created_at");
            b.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            // Supports the FEFO scan: expiry ASC (nulls last), created_at, then the PK entry_id.
            // The composite index's leading household_id column also covers the single-column case.
            b.HasIndex(e => new { e.HouseholdId, e.ProductId, e.ExpiryDate, e.CreatedAt })
                .HasDatabaseName("ix_stock_entry_fefo");

            b.HasQueryFilter(e => e.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<StockJournalEntry>(b =>
        {
            b.ToTable("stock_journal_entry");
            b.HasKey(j => j.Id);
            b.Property(j => j.Id)
                .HasConversion(id => id.Value, v => JournalId.From(v))
                .HasColumnName("journal_id")
                .ValueGeneratedNever();
            b.Property(j => j.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(j => j.ProductId).HasColumnName("product_id").IsRequired();
            b.Property(j => j.StockEntryId)
                .HasConversion(id => id.Value, v => StockEntryId.From(v))
                .HasColumnName("entry_id")
                .IsRequired();
            b.Property(j => j.Delta).HasColumnName("delta").HasPrecision(12, 3);
            b.Property(j => j.UnitId).HasColumnName("unit_id").IsRequired();
            b.Property(j => j.Reason)
                .HasConversion(r => r.ToDbValue(), v => StockReasonExtensions.Parse(v))
                .HasColumnName("reason")
                .HasMaxLength(20)
                .IsRequired();
            b.Property(j => j.SourceType)
                .HasConversion(
                    s => s == null ? null : s.Value.ToDbValue(),
                    v => v == null ? (StockSourceType?)null : StockSourceTypeExtensions.Parse(v))
                .HasColumnName("source_type")
                .HasMaxLength(20);
            b.Property(j => j.SourceRef).HasColumnName("source_ref");
            b.Property(j => j.SourceLineRef).HasColumnName("source_line_ref");
            b.Property(j => j.OccurredAt).HasColumnName("occurred_at");
            b.Property(j => j.UserId).HasColumnName("user_id").IsRequired();

            // Every journal row points at a live lot (DM-14) — enforced FK to stock_entry, no navigation.
            // NoAction (not Cascade): the journal is already cascade-owned by product_stock above, so
            // deleting a root removes both children without a second cascade path through stock_entry.
            b.HasOne<StockEntry>()
                .WithMany()
                .HasForeignKey(j => j.StockEntryId)
                .HasPrincipalKey(e => e.Id)
                .OnDelete(DeleteBehavior.NoAction);

            b.HasIndex(j => j.HouseholdId);
            b.HasIndex(j => new { j.HouseholdId, j.ProductId });
            b.HasIndex(j => j.StockEntryId);
            // Idempotency lookup: for a given household + cook event (source_ref) + line (source_line_ref),
            // find whether any journal row already carries this token (plantry-292a).
            b.HasIndex(j => new { j.HouseholdId, j.SourceRef, j.SourceLineRef })
                .HasDatabaseName("ix_stock_journal_idempotency");

            b.HasQueryFilter(j => j.HouseholdId == HouseholdId.From(_householdId));
        });
    }

    // Populated by the RLS middleware before each request
    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}
