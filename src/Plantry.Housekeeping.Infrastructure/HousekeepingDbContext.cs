using Microsoft.EntityFrameworkCore;
using Plantry.Housekeeping.Domain;
using Plantry.SharedKernel;

namespace Plantry.Housekeeping.Infrastructure;

/// <summary>
/// EF DbContext for the Housekeeping bounded context (<c>housekeeping</c> schema). Owns exactly one
/// aggregate — <see cref="Dismissal"/> — the tombstone table (T4/T5/T9): Tidy Up findings themselves are
/// never persisted.
/// <para>
/// The RlsMiddleware MUST call <see cref="SetHouseholdId"/> on this context for every authenticated
/// request, exactly as for the other bounded-context DbContexts (the known P2-0/P3-0 gotcha: omitting
/// it leaves _householdId as Guid.Empty and every EF query filter returns nothing).
/// </para>
/// </summary>
public sealed class HousekeepingDbContext(DbContextOptions<HousekeepingDbContext> options) : DbContext(options)
{
    public DbSet<Dismissal> Dismissals => Set<Dismissal>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("housekeeping");

        builder.Entity<Dismissal>(b =>
        {
            b.ToTable("dismissal");
            b.HasKey(d => d.Id);
            b.Property(d => d.Id)
                .HasConversion(id => id.Value, v => DismissalId.From(v))
                .HasColumnName("dismissal_id")
                .ValueGeneratedNever();
            b.Property(d => d.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(d => d.DetectorId)
                .HasConversion(id => id.Value, v => new DetectorId(v))
                .HasColumnName("detector_id")
                .IsRequired();
            b.Property(d => d.SubjectId).HasColumnName("subject_id").IsRequired();
            b.Property(d => d.FactsFingerprint).HasColumnName("facts_fingerprint").IsRequired();
            b.Property(d => d.DismissedAtUtc).HasColumnName("dismissed_at_utc").IsRequired();

            // One tombstone per finding key at any time (T5) — Dismiss upserts via Supersede rather
            // than inserting a second row for the same (household, detector, subject).
            b.HasIndex(d => new { d.HouseholdId, d.DetectorId, d.SubjectId })
                .IsUnique()
                .HasDatabaseName("ux_dismissal_household_detector_subject");

            b.HasQueryFilter(d => d.HouseholdId == HouseholdId.From(_householdId));
        });
    }

    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}
