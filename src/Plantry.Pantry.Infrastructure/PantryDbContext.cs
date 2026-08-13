using Microsoft.EntityFrameworkCore;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;

namespace Plantry.Pantry.Infrastructure;

/// <summary>
/// The single EF DbContext for the Pantry bounded context (ADR-024 Phase C). Unifies what were
/// formerly two separate DbContexts — <c>CatalogDbContext</c> and <c>InventoryDbContext</c> — kept
/// apart only during the interim (plantry-g3da.6) merge to avoid touching migration history. This
/// context owns both physical schemas unchanged (plantry-g3da.10 does not move data — see ADR-024
/// §"Physical schemas do not move on day one"):
/// <list type="bullet">
/// <item><b>catalog</b> — <see cref="Unit"/>, <see cref="Category"/>, <see cref="Location"/>,
/// <see cref="Store"/>, <see cref="Product"/> (with <see cref="ProductSku"/> and
/// <see cref="ProductConversion"/> children).</item>
/// <item><b>inventory</b> — <see cref="ProductStock"/> (aggregate root, composite key
/// <c>household_id, product_id</c>) with its <see cref="StockEntry"/> lots and the append-only
/// <see cref="StockJournalEntry"/>, plus the flat <see cref="HouseholdInventorySettings"/> aggregate.
/// Inventory's <c>product_id</c>/<c>unit_id</c>/<c>location_id</c> references into <c>catalog.*</c> are
/// soft (DM-3: no enforced cross-context FK) — sharing one DbContext does not change that; no
/// navigation or FK was added by this unification.</item>
/// </list>
/// The EF migrations-history table (<c>__EFMigrationsHistory</c>) lives in the <c>catalog</c> schema —
/// this context's default schema — reusing the location the old <c>CatalogDbContext</c> already used,
/// rather than introducing a third schema purely for bookkeeping.
/// <para>
/// The RlsMiddleware MUST call <see cref="SetHouseholdId"/> on this context for every authenticated
/// request, exactly as for the other bounded-context DbContexts (the known P2-0/P3-0 gotcha: omitting
/// it leaves _householdId as Guid.Empty and every EF query filter returns nothing).
/// </para>
/// </summary>
public sealed class PantryDbContext(DbContextOptions<PantryDbContext> options) : DbContext(options)
{
    // ── Catalog ──────────────────────────────────────────────────────────────
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Product> Products => Set<Product>();

    // ── Inventory ────────────────────────────────────────────────────────────
    public DbSet<ProductStock> ProductStocks => Set<ProductStock>();
    public DbSet<StockEntry> StockEntries => Set<StockEntry>();
    public DbSet<StockJournalEntry> StockJournalEntries => Set<StockJournalEntry>();
    public DbSet<HouseholdInventorySettings> HouseholdInventorySettings => Set<HouseholdInventorySettings>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Default schema covers Catalog; Inventory entities are schema-qualified explicitly below —
        // there is no single default schema for a context spanning two physical schemas.
        builder.HasDefaultSchema("catalog");

        // ── Unit aggregate root (catalog schema) ────────────────────────────────
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
            // Display style (quantity-display.md Q2): C# enum persisted as text + CHECK (Gate 7),
            // never a Postgres ENUM. The CHECK constraint lives in the migration, matching the
            // ProductConversion.Source convention.
            b.Property(u => u.DisplayStyle)
                .HasConversion(s => s.ToDbValue(), v => DisplayStyleExtensions.Parse(v))
                .HasColumnName("display_style")
                .HasMaxLength(20)
                .IsRequired();
            // Unit system (quantity-display.md Q5): C# enum persisted as text + CHECK (Gate 7), the
            // metric/imperial simplification firewall. CHECK constraint lives in the migration.
            b.Property(u => u.UnitSystem)
                .HasConversion(s => s.ToDbValue(), v => UnitSystemExtensions.Parse(v))
                .HasColumnName("unit_system")
                .HasMaxLength(20)
                .IsRequired();

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
            b.Property(l => l.LastCountedAt).HasColumnName("last_counted_at");

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
            b.Property(p => p.IsProduced).HasColumnName("is_produced");
            b.Property(p => p.DefaultDueDays).HasColumnName("default_due_days");
            b.Property(p => p.DefaultDueDaysAfterOpening).HasColumnName("default_due_days_after_opening");
            b.Property(p => p.DefaultDueDaysAfterFreezing).HasColumnName("default_due_days_after_freezing");
            b.Property(p => p.DefaultDueDaysAfterThawing).HasColumnName("default_due_days_after_thawing");
            b.Property(p => p.NeverExpiresAfterFreezing).HasColumnName("never_expires_after_freezing");
            b.Property(p => p.NeverExpiresAfterThawing).HasColumnName("never_expires_after_thawing");
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
            // Provenance (ADR-022): C# enum persisted as text + CHECK constraint (Gate 7 convention).
            b.Property(c => c.Source)
                .HasConversion(s => s.ToDbValue(), v => ConversionSourceExtensions.Parse(v))
                .HasColumnName("source")
                .HasMaxLength(20)
                .IsRequired();

            b.HasIndex(c => c.HouseholdId);
            b.HasIndex(c => c.ProductId);

            // ADR-022 amendment (plantry-pcfe): a product may hold at most one conversion per
            // UNORDERED unit pair. That is enforced by a unique EXPRESSION index over
            // (product_id, LEAST(from_unit_id, to_unit_id), GREATEST(from_unit_id, to_unit_id)),
            // added by the baseline migration via raw SQL — HasIndex/the fluent API cannot express
            // an expression index, so it is intentionally absent here and from
            // PantryDbContextModelSnapshot. Do NOT add a plain HasIndex on the ordered
            // (FromUnitId, ToUnitId) triple as a "fix" for this gap — it would be redundant with the
            // ProductId index above and would wrongly imply ordered-triple semantics, which
            // Product.AddConversion's merge rule explicitly rejects.

            b.HasQueryFilter(c => c.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── ProductStock aggregate root (inventory schema) ──────────────────────
        builder.Entity<ProductStock>(b =>
        {
            b.ToTable("product_stock", "inventory");

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
            b.Property(p => p.LowStockThreshold).HasColumnName("low_stock_threshold").HasPrecision(12, 3);

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
            b.ToTable("stock_entry", "inventory");
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

            // TS-S2: Take Stock location scan — filters only active (non-depleted) lots so the
            // index is small and the walk query never hits depleted rows.
            b.HasIndex(e => new { e.HouseholdId, e.LocationId, e.ProductId })
                .HasDatabaseName("ix_stock_entry_by_location")
                .HasFilter("depleted_at IS NULL");

            b.HasQueryFilter(e => e.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<StockJournalEntry>(b =>
        {
            b.ToTable("stock_journal_entry", "inventory");
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

        // ── HouseholdInventorySettings aggregate root (plantry-5yhd) ─────────────
        // One row per household, seeded lazily on first write. HouseholdId is both the PK and the
        // aggregate identity (mirrors HouseholdPlanningSettings in the meal_planning context).
        builder.Entity<HouseholdInventorySettings>(b =>
        {
            b.ToTable("household_inventory_settings", "inventory");
            b.HasKey(s => s.HouseholdId);
            b.Property(s => s.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .ValueGeneratedNever();
            b.Property(s => s.ExpiringSoonDays).HasColumnName("expiring_soon_days").IsRequired();
            // Household-wide default storage location (plantry-iypo) — nullable bare LocationId ref,
            // no FK (a soft cross-schema reference into catalog.locations, DM-3 — unchanged by this
            // DbContext unification; sharing one DbContext does not add an FK). Every pre-existing row
            // backfills to NULL (unset).
            b.Property(s => s.DefaultLocationId)
                .HasConversion(id => id == null ? (Guid?)null : id.Value.Value, v => v == null ? (LocationId?)null : LocationId.From(v.Value))
                .HasColumnName("default_location_id");
            b.Property(s => s.DefaultProducedCategoryId)
                .HasConversion(id => id == null ? (Guid?)null : id.Value.Value, v => v == null ? (CategoryId?)null : CategoryId.From(v.Value))
                .HasColumnName("default_produced_category_id");

            b.HasQueryFilter(s => s.HouseholdId == HouseholdId.From(_householdId));
        });
    }

    // Populated by the RLS middleware before each request
    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}
