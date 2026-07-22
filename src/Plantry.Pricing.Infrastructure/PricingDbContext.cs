using Microsoft.EntityFrameworkCore;
using Plantry.Pricing.Domain;
using Plantry.SharedKernel;

namespace Plantry.Pricing.Infrastructure;

/// <summary>
/// EF DbContext for the Pricing bounded context.
/// Owns: price_observation (append-only aggregate root, no children).
/// </summary>
public sealed class PricingDbContext(DbContextOptions<PricingDbContext> options) : DbContext(options)
{
    public DbSet<PriceObservation> PriceObservations => Set<PriceObservation>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("pricing");

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
    }

    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}
