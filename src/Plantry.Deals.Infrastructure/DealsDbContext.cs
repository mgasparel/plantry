using Microsoft.EntityFrameworkCore;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;

namespace Plantry.Deals.Infrastructure;

/// <summary>
/// EF DbContext for the Deals bounded context (<c>deals</c> schema). Owns four <b>flat</b> aggregates —
/// <see cref="StoreSubscription"/>, <see cref="FlyerImport"/>, <see cref="Deal"/>,
/// <see cref="DealMatchMemory"/> — none with child entities (domain model §2). The one enforced
/// cross-aggregate FK, <c>deal.flyer_import_id → flyer_import(household_id, flyer_import_id)</c>
/// (RESTRICT, nullable), is created in the migration as raw SQL and has <b>no</b> EF navigation (the
/// deliberate flat-aggregate split from Intake).
/// <para>
/// The RlsMiddleware MUST call <see cref="SetHouseholdId"/> on this context for every authenticated
/// request, exactly as for the other bounded-context DbContexts (the known P2-0/P3-0 gotcha: omitting
/// it leaves _householdId as Guid.Empty and every EF query filter returns nothing).
/// </para>
/// </summary>
public sealed class DealsDbContext(DbContextOptions<DealsDbContext> options) : DbContext(options)
{
    public DbSet<StoreSubscription> StoreSubscriptions => Set<StoreSubscription>();
    public DbSet<FlyerImport> FlyerImports => Set<FlyerImport>();
    public DbSet<Deal> Deals => Set<Deal>();
    public DbSet<DealMatchMemory> DealMatchMemories => Set<DealMatchMemory>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("deals");

        // ── StoreSubscription aggregate root ─────────────────────────────────────
        builder.Entity<StoreSubscription>(b =>
        {
            b.ToTable("store_subscription");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id)
                .HasConversion(id => id.Value, v => StoreSubscriptionId.From(v))
                .HasColumnName("store_subscription_id")
                .ValueGeneratedNever();
            b.Property(s => s.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(s => s.StoreId).HasColumnName("store_id").IsRequired();
            b.Property(s => s.PostalCode).HasColumnName("postal_code").IsRequired();
            b.Property(s => s.IsActive).HasColumnName("is_active").IsRequired().HasDefaultValue(true);
            b.Property(s => s.LastPulledAt).HasColumnName("last_pulled_at");
            b.Property(s => s.LastFlyerExternalId).HasColumnName("last_flyer_external_id");
            b.Property(s => s.CreatedAt).HasColumnName("created_at");
            b.Property(s => s.UpdatedAt).HasColumnName("updated_at");

            // UNIQUE (household_id, store_id) — one subscription per merchant (DD9)
            b.HasIndex(s => new { s.HouseholdId, s.StoreId })
                .IsUnique()
                .HasDatabaseName("ux_store_subscription_household_store");

            b.HasQueryFilter(s => s.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── FlyerImport aggregate root ───────────────────────────────────────────
        builder.Entity<FlyerImport>(b =>
        {
            b.ToTable("flyer_import");
            b.HasKey(f => f.Id);
            b.Property(f => f.Id)
                .HasConversion(id => id.Value, v => FlyerImportId.From(v))
                .HasColumnName("flyer_import_id")
                .ValueGeneratedNever();
            b.Property(f => f.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(f => f.StoreId).HasColumnName("store_id").IsRequired();
            b.Property(f => f.FlyerExternalId).HasColumnName("flyer_external_id").IsRequired();
            b.Property(f => f.ContentHash).HasColumnName("content_hash");
            b.OwnsOne(f => f.ValidityWindow, w =>
            {
                w.Property(v => v.ValidFrom).HasColumnName("valid_from").IsRequired();
                w.Property(v => v.ValidTo).HasColumnName("valid_to").IsRequired();
            });
            b.Navigation(f => f.ValidityWindow).IsRequired();
            b.Property(f => f.RawFlyer).HasColumnName("raw_flyer").HasColumnType("jsonb").IsRequired();
            b.Property(f => f.Status)
                .HasConversion(s => s.ToString().ToLowerInvariant(), v => Enum.Parse<PullStatus>(v, ignoreCase: true))
                .HasColumnName("status")
                .IsRequired();
            b.Property(f => f.ErrorDetail).HasColumnName("error_detail");
            b.Property(f => f.PulledAt).HasColumnName("pulled_at");
            b.Property(f => f.ParsedAt).HasColumnName("parsed_at");
            b.Property(f => f.CreatedAt).HasColumnName("created_at");
            b.Property(f => f.UpdatedAt).HasColumnName("updated_at");

            // UNIQUE (household_id, store_id, flyer_external_id) — the dedup key (DD5)
            b.HasIndex(f => new { f.HouseholdId, f.StoreId, f.FlyerExternalId })
                .IsUnique()
                .HasDatabaseName("ux_flyer_import_household_store_external");

            // UNIQUE (household_id, flyer_import_id) — anchor for the deal composite FK
            b.HasIndex(f => new { f.HouseholdId, f.Id })
                .IsUnique()
                .HasDatabaseName("ux_flyer_import_household_id");

            b.HasQueryFilter(f => f.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── Deal aggregate root ──────────────────────────────────────────────────
        builder.Entity<Deal>(b =>
        {
            b.ToTable("deal");
            b.HasKey(d => d.Id);
            b.Property(d => d.Id)
                .HasConversion(id => id.Value, v => DealId.From(v))
                .HasColumnName("deal_id")
                .ValueGeneratedNever();
            b.Property(d => d.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            // Within-context composite FK anchor; nullable for the deferred manual path (D12).
            // The FK constraint itself is created in the migration as raw SQL (no EF navigation).
            b.Property(d => d.FlyerImportId)
                .HasConversion(
                    id => id.HasValue ? id.Value.Value : (Guid?)null,
                    v => v.HasValue ? FlyerImportId.From(v.Value) : (FlyerImportId?)null)
                .HasColumnName("flyer_import_id");
            b.Property(d => d.StoreId).HasColumnName("store_id").IsRequired();
            b.Property(d => d.Source)
                .HasConversion(s => s.ToString().ToLowerInvariant(), v => Enum.Parse<DealSource>(v, ignoreCase: true))
                .HasColumnName("source")
                .IsRequired();

            // Raw flyer fields (ACL, read-only after parse)
            b.Property(d => d.RawName).HasColumnName("raw_name").IsRequired();
            b.Property(d => d.Brand).HasColumnName("brand");
            b.Property(d => d.Size).HasColumnName("size");
            b.Property(d => d.Price).HasColumnName("price").HasPrecision(12, 2);
            b.Property(d => d.Quantity).HasColumnName("quantity").HasPrecision(12, 3);
            b.Property(d => d.UnitId).HasColumnName("unit_id");
            b.Property(d => d.SaleStory).HasColumnName("sale_story");
            b.Property(d => d.NormalizedName).HasColumnName("normalized_name").IsRequired();

            // Match proposal (ACL quarantine)
            b.Property(d => d.SuggestedProductId).HasColumnName("suggested_product_id");
            b.Property(d => d.MatchConfidence)
                .HasConversion(c => c.ToString().ToLowerInvariant(), v => Enum.Parse<MatchConfidence>(v, ignoreCase: true))
                .HasColumnName("match_confidence")
                .IsRequired();
            b.Property(d => d.MatchReasoning).HasColumnName("match_reasoning");

            // User-resolved
            b.Property(d => d.ProductId).HasColumnName("product_id");

            // Lifecycle & linkage
            b.Property(d => d.Status)
                .HasConversion(s => s.ToString().ToLowerInvariant(), v => Enum.Parse<DealStatus>(v, ignoreCase: true))
                .HasColumnName("status")
                .IsRequired();
            b.OwnsOne(d => d.ValidityWindow, w =>
            {
                w.Property(v => v.ValidFrom).HasColumnName("valid_from").IsRequired();
                w.Property(v => v.ValidTo).HasColumnName("valid_to").IsRequired();
            });
            b.Navigation(d => d.ValidityWindow).IsRequired();
            b.Property(d => d.CommittedPriceObservationId).HasColumnName("committed_price_observation_id");
            b.Property(d => d.AutoMatched).HasColumnName("auto_matched").IsRequired().HasDefaultValue(false);
            b.Property(d => d.ReviewedByUserId).HasColumnName("reviewed_by_user_id");
            b.Property(d => d.ReviewedAt).HasColumnName("reviewed_at");
            b.Property(d => d.CreatedAt).HasColumnName("created_at");
            b.Property(d => d.UpdatedAt).HasColumnName("updated_at");

            b.HasIndex(d => new { d.HouseholdId, d.FlyerImportId })
                .HasDatabaseName("ix_deal_household_flyer_import");
            b.HasIndex(d => new { d.HouseholdId, d.StoreId, d.Status })
                .HasDatabaseName("ix_deal_household_store_status");

            b.HasQueryFilter(d => d.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── DealMatchMemory aggregate root ───────────────────────────────────────
        builder.Entity<DealMatchMemory>(b =>
        {
            b.ToTable("deal_match_memory");
            b.HasKey(m => m.Id);
            b.Property(m => m.Id)
                .HasConversion(id => id.Value, v => DealMatchMemoryId.From(v))
                .HasColumnName("deal_match_memory_id")
                .ValueGeneratedNever();
            b.Property(m => m.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(m => m.StoreId).HasColumnName("store_id").IsRequired();
            b.Property(m => m.NormalizedName).HasColumnName("normalized_name").IsRequired();
            b.Property(m => m.RawName).HasColumnName("raw_name").IsRequired();
            b.Property(m => m.NormalizerVersion).HasColumnName("normalizer_version").IsRequired();
            b.Property(m => m.ProductId).HasColumnName("product_id");
            b.Property(m => m.LastConfirmedByUserId).HasColumnName("last_confirmed_by_user_id");
            b.Property(m => m.CreatedAt).HasColumnName("created_at");
            b.Property(m => m.UpdatedAt).HasColumnName("updated_at");

            // UNIQUE (household_id, store_id, normalized_name) — the auto-confirm key (DD3)
            b.HasIndex(m => new { m.HouseholdId, m.StoreId, m.NormalizedName })
                .IsUnique()
                .HasDatabaseName("ux_deal_match_memory_household_store_name");

            b.HasQueryFilter(m => m.HouseholdId == HouseholdId.From(_householdId));
        });
    }

    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}
