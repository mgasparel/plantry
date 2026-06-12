using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;

namespace Plantry.Intake.Infrastructure;

/// <summary>
/// EF DbContext for the Intake bounded context.
/// Owns: import_session (aggregate root) + import_line (child collection) + import_receipt (1:1, hot-path separated).
/// </summary>
public sealed class IntakeDbContext(DbContextOptions<IntakeDbContext> options) : DbContext(options)
{
    public DbSet<ImportSession> ImportSessions => Set<ImportSession>();
    public DbSet<ImportLine> ImportLines => Set<ImportLine>();
    public DbSet<ImportReceipt> ImportReceipts => Set<ImportReceipt>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("intake");

        builder.Entity<ImportSession>(b =>
        {
            b.ToTable("import_session");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id)
                .HasConversion(id => id.Value, v => ImportSessionId.From(v))
                .HasColumnName("session_id")
                .ValueGeneratedNever();
            b.Property(s => s.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(s => s.SourceType)
                .HasConversion(t => t.ToDbValue(), v => ImportSourceTypeExtensions.Parse(v))
                .HasColumnName("source_type")
                .HasMaxLength(20)
                .IsRequired();
            b.Property(s => s.UserId).HasColumnName("user_id").IsRequired();
            b.Property(s => s.Status)
                .HasConversion(s => s.ToDbValue(), v => ImportStatusExtensions.Parse(v))
                .HasColumnName("status")
                .HasMaxLength(20)
                .IsRequired();
            b.Property(s => s.MerchantText).HasColumnName("merchant_text").HasMaxLength(200);
            b.Property(s => s.ParseError).HasColumnName("parse_error").HasMaxLength(2000);
            b.Property(s => s.ParsedAt).HasColumnName("parsed_at");
            b.Property(s => s.CommittedAt).HasColumnName("committed_at");
            b.Property(s => s.CreatedAt).HasColumnName("created_at");
            b.Property(s => s.UpdatedAt).HasColumnName("updated_at");

            b.HasMany(s => s.Lines)
                .WithOne()
                .HasForeignKey(l => l.SessionId)
                .HasPrincipalKey(s => s.Id)
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(s => s.Lines)
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasField("_lines");

            b.HasIndex(s => s.HouseholdId).HasDatabaseName("ix_import_session_household");
            b.HasQueryFilter(s => s.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<ImportLine>(b =>
        {
            b.ToTable("import_line");
            b.HasKey(l => l.Id);
            b.Property(l => l.Id)
                .HasConversion(id => id.Value, v => ImportLineId.From(v))
                .HasColumnName("line_id")
                .ValueGeneratedNever();
            b.Property(l => l.SessionId)
                .HasConversion(id => id.Value, v => ImportSessionId.From(v))
                .HasColumnName("session_id")
                .IsRequired();
            b.Property(l => l.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(l => l.LineNo).HasColumnName("line_no").IsRequired();
            b.Property(l => l.ReceiptText).HasColumnName("receipt_text").HasMaxLength(500).IsRequired();
            b.Property(l => l.SuggestedConfidence)
                .HasConversion(c => c.ToDbValue(), v => SuggestedConfidenceExtensions.Parse(v))
                .HasColumnName("suggested_confidence")
                .HasMaxLength(10)
                .IsRequired();
            // raw_parse is stored as jsonb and never overwritten after creation (ACL invariant).
            b.Property(l => l.RawParse)
                .HasColumnName("raw_parse")
                .HasColumnType("jsonb");
            b.Property(l => l.SuggestedProductId).HasColumnName("suggested_product_id");
            b.Property(l => l.SuggestedProductName).HasColumnName("suggested_product_name").HasMaxLength(200);
            b.Property(l => l.SuggestedQuantity).HasColumnName("suggested_quantity").HasPrecision(12, 3);
            b.Property(l => l.SuggestedUnitLabel).HasColumnName("suggested_unit_label").HasMaxLength(20);
            b.Property(l => l.SuggestedPrice).HasColumnName("suggested_price").HasPrecision(12, 2);
            // suggested_alternatives is stored as jsonb — AI-populated at parse time, never overwritten.
            b.Property(l => l.SuggestedAlternatives)
                .HasColumnName("suggested_alternatives")
                .HasColumnType("jsonb")
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<List<AlternativeCandidate>>(v, (JsonSerializerOptions?)null),
                    new ValueComparer<IReadOnlyList<AlternativeCandidate>?>(
                        (l, r) => (l == null && r == null) || (l != null && r != null && l.SequenceEqual(r)),
                        l => l == null ? 0 : l.Aggregate(0, (h, c) => HashCode.Combine(h, c.GetHashCode())),
                        l => l == null ? null : l.ToList()));
            b.Property(l => l.ProductId).HasColumnName("product_id");
            b.Property(l => l.SkuId).HasColumnName("sku_id");
            b.Property(l => l.Quantity).HasColumnName("quantity").HasPrecision(12, 3);
            b.Property(l => l.UnitId).HasColumnName("unit_id");
            b.Property(l => l.LocationId).HasColumnName("location_id");
            b.Property(l => l.ExpiryDate).HasColumnName("expiry_date");
            b.Property(l => l.Price).HasColumnName("price").HasPrecision(12, 2);
            b.Property(l => l.NewProductName).HasColumnName("new_product_name").HasMaxLength(200);
            b.Property(l => l.NewProductCategoryId).HasColumnName("new_product_category_id");
            b.Property(l => l.Status)
                .HasConversion(s => s.ToDbValue(), v => LineStatusExtensions.Parse(v))
                .HasColumnName("status")
                .HasMaxLength(20)
                .IsRequired();
            b.Property(l => l.JournalId).HasColumnName("journal_id");
            b.Property(l => l.PriceObservationId).HasColumnName("price_observation_id");
            b.Property(l => l.CreatedProductId).HasColumnName("created_product_id");

            b.HasIndex(l => l.SessionId).HasDatabaseName("ix_import_line_session");
            b.HasQueryFilter(l => l.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<ImportReceipt>(b =>
        {
            b.ToTable("import_receipt");
            b.HasKey(r => r.Id);
            b.Property(r => r.Id)
                .HasConversion(id => id.Value, v => ImportSessionId.From(v))
                .HasColumnName("session_id")
                .ValueGeneratedNever();
            b.Property(r => r.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(r => r.Content).HasColumnName("content").IsRequired();
            b.Property(r => r.ContentType).HasColumnName("content_type").HasMaxLength(100).IsRequired();
            b.Property(r => r.ByteSize).HasColumnName("byte_size").IsRequired();
            b.Property(r => r.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
            b.Property(r => r.RawText).HasColumnName("raw_text");

            // 1:1 FK — receipt shares PK with session; cascade so deleting session removes receipt.
            b.HasOne<ImportSession>()
                .WithOne()
                .HasForeignKey<ImportReceipt>(r => r.Id)
                .HasPrincipalKey<ImportSession>(s => s.Id)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasQueryFilter(r => r.HouseholdId == HouseholdId.From(_householdId));
        });
    }

    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}
