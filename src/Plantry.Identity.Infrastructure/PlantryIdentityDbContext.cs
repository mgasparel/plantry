using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;

namespace Plantry.Identity.Infrastructure;

public sealed class PlantryIdentityDbContext(DbContextOptions<PlantryIdentityDbContext> options)
    : IdentityDbContext<AppUser>(options)
{
    public DbSet<Household> Households => Set<Household>();
    public DbSet<HouseholdInvite> HouseholdInvites => Set<HouseholdInvite>();

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
            // Household-wide assistive-AI switch (plantry-qll2.1). Store default true backfills
            // pre-existing households to ON; new households are inserted with the aggregate default (true).
            b.Property(h => h.AiAssistanceEnabled)
                .HasColumnName("ai_assistance_enabled")
                .HasDefaultValue(true);
            b.Property(h => h.CreatedAt).HasColumnName("created_at");

            // App-layer half of the defense-in-depth pair (the Postgres RLS policy is the other).
            // Keyed on the row's own id since households *is* the tenant anchor. Fail-closed: with
            // no household set, _householdId is Guid.Empty and no row matches.
            b.HasQueryFilter(h => h.Id == HouseholdId.From(_householdId));
        });

        builder.Entity<HouseholdInvite>(b =>
        {
            b.ToTable("household_invites", t =>
                t.HasCheckConstraint(
                    "ck_household_invites_status",
                    "status IN ('pending','accepted','revoked','expired')"));
            b.HasKey(i => i.Id);
            b.Property(i => i.Id)
                .HasConversion(id => id.Value, v => HouseholdInviteId.From(v))
                .HasColumnName("id");
            b.Property(i => i.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(i => i.Email).HasColumnName("email").HasMaxLength(254).IsRequired();
            b.Property(i => i.Token).HasColumnName("token").HasMaxLength(128).IsRequired();
            b.Property(i => i.Status)
                .HasConversion(s => s.ToDbValue(), v => InviteStatusExtensions.Parse(v))
                .HasColumnName("status")
                .HasMaxLength(20)
                .IsRequired();
            b.Property(i => i.InvitedByUserId).HasColumnName("invited_by_user_id").IsRequired();
            b.Property(i => i.CreatedAt).HasColumnName("created_at");
            b.Property(i => i.ExpiresAt).HasColumnName("expires_at");
            b.Property(i => i.AcceptedAt).HasColumnName("accepted_at");
            // Audit link invite → joining member, stamped inside the join transaction (plantry-bmfg).
            // Nullable: only accepted invites carry it; pre-existing rows backfill to NULL.
            b.Property(i => i.AcceptedByUserId).HasColumnName("accepted_by_user_id");

            // Single-use backstop (plantry-bmfg): Postgres' xmin system column as an optimistic-concurrency
            // token, mirroring inventory.product_stock. No stored column, no migration, no app-side
            // increment — Npgsql maps this uint shadow property to the system column and composes it into
            // the concurrency-guarded UPDATE. Two concurrent accepts of the same token both read the same
            // xmin; the winner's commit bumps it, so the loser's UPDATE (…WHERE xmin = old) matches zero
            // rows and EF raises DbUpdateConcurrencyException. Hardens Accept AND the Accept-vs-Revoke race.
            b.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

            // R4: the accept-link token is globally unique across households.
            b.HasIndex(i => i.Token).IsUnique().HasDatabaseName("ux_household_invites_token");
            b.HasIndex(i => i.HouseholdId).HasDatabaseName("ix_household_invites_household_id");

            // App-layer half of the defense-in-depth pair, keyed on the parent household id (same as
            // the Postgres RLS carve-out policy). Fail-closed under a scoped context; the token accept
            // path deliberately IgnoreQueryFilters() and runs with no tenant so the carve-out applies.
            b.HasQueryFilter(i => i.HouseholdId == HouseholdId.From(_householdId));
        });
    }

    // Populated by the RLS middleware before each request; feeds the Household query filter.
    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}
