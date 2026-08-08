using Microsoft.EntityFrameworkCore;
using Plantry.Market.Domain;
using Plantry.SharedKernel;

namespace Plantry.Market.Infrastructure;

/// <summary>
/// The single EF DbContext for the Market bounded context (ADR-024 Phase A). Unifies what were
/// formerly two separate DbContexts — <c>PricingDbContext</c> and <c>DealsDbContext</c> — kept apart
/// only during the interim (plantry-g3da.1) merge to avoid touching migration history. This context
/// owns both physical schemas unchanged (plantry-g3da.7 does not move data — see ADR-024 §"Physical
/// schemas do not move on day one"):
/// <list type="bullet">
/// <item><b>pricing</b> — <see cref="PriceObservation"/> (append-only aggregate root, no children).</item>
/// <item><b>deals</b> — four <b>flat</b> aggregates: <see cref="StoreSubscription"/>,
/// <see cref="FlyerImport"/>, <see cref="Deal"/>, <see cref="DealMatchMemory"/> (domain model §2). The
/// one enforced cross-aggregate FK, <c>deal.flyer_import_id → flyer_import(household_id, flyer_import_id)</c>
/// (RESTRICT, nullable), is created in the baseline migration as raw SQL and has <b>no</b> EF navigation
/// (the deliberate flat-aggregate split from Intake).</item>
/// </list>
/// The EF migrations-history table (<c>__EFMigrationsHistory</c>) lives in the <c>pricing</c> schema —
/// this context's default schema — reusing the location the old <c>PricingDbContext</c> already used,
/// rather than introducing a third schema purely for bookkeeping.
/// <para>
/// The RlsMiddleware MUST call <see cref="SetHouseholdId"/> on this context for every authenticated
/// request, exactly as for the other bounded-context DbContexts (the known P2-0/P3-0 gotcha: omitting
/// it leaves _householdId as Guid.Empty and every EF query filter returns nothing).
/// </para>
/// </summary>
public sealed class MarketDbContext(DbContextOptions<MarketDbContext> options) : DbContext(options)
{
    // ── Pricing ──────────────────────────────────────────────────────────────
    public DbSet<PriceObservation> PriceObservations => Set<PriceObservation>();

    // ── Deals ────────────────────────────────────────────────────────────────
    public DbSet<StoreSubscription> StoreSubscriptions => Set<StoreSubscription>();
    public DbSet<FlyerImport> FlyerImports => Set<FlyerImport>();
    public DbSet<Deal> Deals => Set<Deal>();
    public DbSet<DealMatchMemory> DealMatchMemories => Set<DealMatchMemory>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Default schema covers Pricing; Deals entities are schema-qualified explicitly below —
        // there is no single default schema for a context spanning two physical schemas.
        builder.HasDefaultSchema("pricing");

        // ── PriceObservation aggregate root (pricing schema) ────────────────────
        builder.Entity<PriceObservation>(b =>
        {
            b.ToTable("price_observation", t =>
                t.HasCheckConstraint("ck_price_observation_valid_window", "valid_from <= valid_to"));
            b.HasKey(p => p.Id);
            b.Property(p => p.Id)
                .HasConversion(id => id.Value, v => PriceObservationId.From(v))
                .HasColumnName("observation_id")
                .ValueGeneratedNever();
            b.Property(p => p.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(p => p.ProductId).HasColumnName("product_id").IsRequired();
            b.Property(p => p.SkuId).HasColumnName("sku_id");
            b.Property(p => p.Price).HasColumnName("price").HasPrecision(12, 2).IsRequired();
            b.Property(p => p.Quantity).HasColumnName("quantity").HasPrecision(12, 3).IsRequired();
            b.Property(p => p.UnitId).HasColumnName("unit_id").IsRequired();
            b.Property(p => p.UnitPrice).HasColumnName("unit_price").HasPrecision(12, 6);
            b.Property(p => p.Source)
                .HasConversion(s => s.ToDbValue(), v => PriceSourceExtensions.Parse(v))
                .HasColumnName("source")
                .HasMaxLength(20)
                .IsRequired();
            b.Property(p => p.MerchantText).HasColumnName("merchant_text").HasMaxLength(200);
            b.Property(p => p.StoreId).HasColumnName("store_id");
            b.Property(p => p.ValidFrom).HasColumnName("valid_from");
            b.Property(p => p.ValidTo).HasColumnName("valid_to");
            // Nullable — a Manual observation (plantry-3fqm) has no source document to point at.
            b.Property(p => p.SourceRef).HasColumnName("source_ref");
            b.Property(p => p.ObservedAt).HasColumnName("observed_at").IsRequired();
            b.Property(p => p.UserId).HasColumnName("user_id").IsRequired();
            // ADR-023 A7 — nullable self-FKs for the amendment supersede chain. Null on every ordinary row.
            b.Property(p => p.AmendsId)
                .HasConversion(id => id!.Value.Value, v => PriceObservationId.From(v))
                .HasColumnName("amends_id");
            b.Property(p => p.SupersededById)
                .HasConversion(id => id!.Value.Value, v => PriceObservationId.From(v))
                .HasColumnName("superseded_by_id");
            // Soft-ref to deals.deal — no FK, same convention as StoreId/SourceRef above (plantry-j9q4).
            b.Property(p => p.MatchedDealId).HasColumnName("matched_deal_id");
            b.HasOne<PriceObservation>()
                .WithMany()
                .HasForeignKey(p => p.AmendsId)
                .HasPrincipalKey(p => p.Id)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PriceObservation>()
                .WithMany()
                .HasForeignKey(p => p.SupersededById)
                .HasPrincipalKey(p => p.Id)
                .OnDelete(DeleteBehavior.Restrict);

            // Latest-price read model: most recent observation per product or SKU.
            b.HasIndex(p => new { p.HouseholdId, p.ProductId, p.ObservedAt })
                .HasDatabaseName("ix_price_observation_product");
            b.HasIndex(p => new { p.HouseholdId, p.SkuId, p.ObservedAt })
                .HasDatabaseName("ix_price_observation_sku")
                .HasFilter("sku_id IS NOT NULL");
            // Cheapest-active-deal read model: deal rows only (source stored as 'Deal', DM-17).
            b.HasIndex(p => new { p.HouseholdId, p.ProductId })
                .HasDatabaseName("ix_price_observation_deal")
                .HasFilter("source = 'Deal'");

            b.HasQueryFilter(p => p.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── StoreSubscription aggregate root (deals schema) ─────────────────────
        builder.Entity<StoreSubscription>(b =>
        {
            b.ToTable("store_subscription", "deals");
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
            b.Property(s => s.LastNewContentAt).HasColumnName("last_new_content_at");
            b.Property(s => s.LastFlyerExternalId).HasColumnName("last_flyer_external_id");
            b.Property(s => s.CreatedAt).HasColumnName("created_at");
            b.Property(s => s.UpdatedAt).HasColumnName("updated_at");

            // UNIQUE (household_id, store_id) — one subscription per merchant (DD9)
            b.HasIndex(s => new { s.HouseholdId, s.StoreId })
                .IsUnique()
                .HasDatabaseName("ux_store_subscription_household_store");

            b.HasQueryFilter(s => s.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── FlyerImport aggregate root (deals schema) ────────────────────────────
        builder.Entity<FlyerImport>(b =>
        {
            b.ToTable("flyer_import", "deals");
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
            // ValidityWindow is a ValueObject shared BY REFERENCE across a pull's FlyerImport and every one of
            // its Deals (FlyerSource.MapFlyer builds ONE instance and hands it to both). Map it as a complex type,
            // NOT an owned entity: owned instances are identity-tracked under a single owner, so the shared window
            // was claimed by the FlyerImport and each Deal INSERT then omitted valid_from/valid_to → not-null
            // violation (plantry-cegw). A complex type has value semantics, so one CLR instance legally backs
            // every owner's columns. Same physical columns (valid_from/valid_to date NOT NULL) — no schema change.
            b.ComplexProperty(f => f.ValidityWindow, w =>
            {
                w.Property(v => v.ValidFrom).HasColumnName("valid_from").IsRequired();
                w.Property(v => v.ValidTo).HasColumnName("valid_to").IsRequired();
            });
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

            // UNIQUE (household_id, store_id, flyer_external_id) WHERE status='parsed' — the dedup key (DD5).
            // Partial so only ONE Parsed envelope may occupy the dedup key; Failed/Pulling rows are excluded, so
            // a materialize fault that records Failed no longer poison-pills the flyer (plantry-0l05). Every Failed
            // attempt is retained as a separate audit row (DD12 stays intact — no row is ever reopened or mutated).
            b.HasIndex(f => new { f.HouseholdId, f.StoreId, f.FlyerExternalId })
                .IsUnique()
                .HasFilter("status = 'parsed'")
                .HasDatabaseName("ux_flyer_import_household_store_external");

            // UNIQUE (household_id, flyer_import_id) — anchor for the deal composite FK
            b.HasIndex(f => new { f.HouseholdId, f.Id })
                .IsUnique()
                .HasDatabaseName("ux_flyer_import_household_id");

            b.HasQueryFilter(f => f.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── Deal aggregate root (deals schema) ───────────────────────────────────
        builder.Entity<Deal>(b =>
        {
            b.ToTable("deal", "deals");
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
            // The FK constraint itself is created in the baseline migration as raw SQL (no EF navigation).
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
            // Complex type, not an owned entity — see the FlyerImport mapping above (plantry-cegw): the window
            // instance is shared with the parent FlyerImport, and value semantics let it back the deal's own
            // valid_from/valid_to columns without being identity-claimed by the import.
            b.ComplexProperty(d => d.ValidityWindow, w =>
            {
                w.Property(v => v.ValidFrom).HasColumnName("valid_from").IsRequired();
                w.Property(v => v.ValidTo).HasColumnName("valid_to").IsRequired();
            });
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

        // ── DealMatchMemory aggregate root (deals schema) ────────────────────────
        builder.Entity<DealMatchMemory>(b =>
        {
            b.ToTable("deal_match_memory", "deals");
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
