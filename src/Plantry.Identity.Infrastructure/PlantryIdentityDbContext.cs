using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;

namespace Plantry.Identity.Infrastructure;

public sealed class PlantryIdentityDbContext(DbContextOptions<PlantryIdentityDbContext> options)
    : IdentityDbContext<AppUser>(options)
{
    public DbSet<Household> Households => Set<Household>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema("identity");

        builder.Entity<AppUser>(b =>
        {
            b.ToTable("users");
            b.Property(u => u.HouseholdId).HasColumnName("household_id").IsRequired();
            b.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(100);
        });

        builder.Entity<Household>(b =>
        {
            b.ToTable("households");
            b.HasKey(h => h.Id);
            b.Property(h => h.Id)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("id");
            b.Property(h => h.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            b.Property(h => h.EmailIntakeAddress).HasColumnName("email_intake_address").HasMaxLength(254);
            b.Property(h => h.ExpiryWarningDays).HasColumnName("expiry_warning_days");
            b.Property(h => h.Theme).HasColumnName("theme").HasMaxLength(20);
            b.Property(h => h.CreatedAt).HasColumnName("created_at");

            // App-layer half of the defense-in-depth pair (the Postgres RLS policy is the other).
            // Keyed on the row's own id since households *is* the tenant anchor. Fail-closed: with
            // no household set, _householdId is Guid.Empty and no row matches.
            b.HasQueryFilter(h => h.Id == HouseholdId.From(_householdId));
        });
    }

    // Populated by the RLS middleware before each request; feeds the Household query filter.
    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}
