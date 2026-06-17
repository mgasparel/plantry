using Microsoft.EntityFrameworkCore;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;

namespace Plantry.MealPlanning.Infrastructure;

/// <summary>
/// EF DbContext for the Meal Planning bounded context (<c>meal_planning</c> schema).
/// Owns seven tables across three aggregates: MealPlan (+ PlannedMeal + PlannedDish),
/// MealSlotConfig (+ MealSlot), and UserPreference (+ TagStance).
/// <para>
/// The RlsMiddleware MUST call <see cref="SetHouseholdId"/> on this context for every authenticated
/// request, exactly as it does for all other bounded-context DbContexts (the known P2-0 gotcha:
/// omitting it leaves _householdId as Guid.Empty and every EF query filter returns nothing).
/// </para>
/// </summary>
public sealed class MealPlanningDbContext(DbContextOptions<MealPlanningDbContext> options) : DbContext(options)
{
    public DbSet<MealPlan> MealPlans => Set<MealPlan>();
    public DbSet<PlannedMeal> PlannedMeals => Set<PlannedMeal>();
    public DbSet<PlannedDish> PlannedDishes => Set<PlannedDish>();
    public DbSet<MealSlotConfig> MealSlotConfigs => Set<MealSlotConfig>();
    public DbSet<MealSlot> MealSlots => Set<MealSlot>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
    public DbSet<TagStance> TagStances => Set<TagStance>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("meal_planning");

        // ── MealPlan aggregate root ─────────────────────────────────────────────
        builder.Entity<MealPlan>(b =>
        {
            b.ToTable("meal_plan");
            b.HasKey(m => m.Id);
            b.Property(m => m.Id)
                .HasConversion(id => id.Value, v => MealPlanId.From(v))
                .HasColumnName("meal_plan_id")
                .ValueGeneratedNever();
            b.Property(m => m.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(m => m.WeekStart).HasColumnName("week_start").IsRequired();
            b.Property(m => m.CreatedAt).HasColumnName("created_at");
            b.Property(m => m.UpdatedAt).HasColumnName("updated_at");

            // UNIQUE (household_id, meal_plan_id) — anchor for composite child FKs
            b.HasIndex(m => new { m.HouseholdId, m.Id })
                .IsUnique()
                .HasDatabaseName("ux_meal_plan_household_id");

            // UNIQUE (household_id, week_start) — at most one plan per household per week (M1)
            b.HasIndex(m => new { m.HouseholdId, m.WeekStart })
                .IsUnique()
                .HasDatabaseName("ux_meal_plan_household_week");

            b.HasMany(m => m.PlannedMeals)
                .WithOne()
                .HasForeignKey(pm => pm.MealPlanId)
                .HasPrincipalKey(m => m.Id)
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(m => m.PlannedMeals)
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasField("_plannedMeals");

            b.HasQueryFilter(m => m.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── PlannedMeal ─────────────────────────────────────────────────────────
        builder.Entity<PlannedMeal>(b =>
        {
            b.ToTable("planned_meal");
            b.HasKey(pm => pm.Id);
            b.Property(pm => pm.Id)
                .HasConversion(id => id.Value, v => PlannedMealId.From(v))
                .HasColumnName("planned_meal_id")
                .ValueGeneratedNever();
            b.Property(pm => pm.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(pm => pm.MealPlanId)
                .HasConversion(id => id.Value, v => MealPlanId.From(v))
                .HasColumnName("meal_plan_id")
                .IsRequired();
            b.Property(pm => pm.Date).HasColumnName("date").IsRequired();
            b.Property(pm => pm.MealSlotId)
                .HasConversion(id => id.Value, v => MealSlotId.From(v))
                .HasColumnName("meal_slot_id")
                .IsRequired();
            b.Property(pm => pm.AttendeesOverride)
                .HasColumnName("attendees_override")
                .HasColumnType("uuid[]");
            b.Property(pm => pm.Reasoning).HasColumnName("reasoning");
            b.Property(pm => pm.Note).HasColumnName("note");
            b.Property(pm => pm.Source).HasColumnName("source").IsRequired();
            b.Property(pm => pm.CreatedBy).HasColumnName("created_by").IsRequired();
            b.Property(pm => pm.UpdatedBy).HasColumnName("updated_by").IsRequired();
            b.Property(pm => pm.CreatedAt).HasColumnName("created_at");
            b.Property(pm => pm.UpdatedAt).HasColumnName("updated_at");

            // UNIQUE (household_id, planned_meal_id) — composite FK anchor
            b.HasIndex(pm => new { pm.HouseholdId, pm.Id })
                .IsUnique()
                .HasDatabaseName("ux_planned_meal_household_id");

            // UNIQUE (meal_plan_id, date, meal_slot_id) — at most one meal per cell (M2)
            b.HasIndex(pm => new { pm.MealPlanId, pm.Date, pm.MealSlotId })
                .IsUnique()
                .HasDatabaseName("ux_planned_meal_plan_date_slot");

            b.HasMany(pm => pm.PlannedDishes)
                .WithOne()
                .HasForeignKey(pd => pd.PlannedMealId)
                .HasPrincipalKey(pm => pm.Id)
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(pm => pm.PlannedDishes)
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasField("_plannedDishes");

            b.HasQueryFilter(pm => pm.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── PlannedDish ─────────────────────────────────────────────────────────
        builder.Entity<PlannedDish>(b =>
        {
            b.ToTable("planned_dish");
            b.HasKey(pd => pd.Id);
            b.Property(pd => pd.Id)
                .HasConversion(id => id.Value, v => PlannedDishId.From(v))
                .HasColumnName("planned_dish_id")
                .ValueGeneratedNever();
            b.Property(pd => pd.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(pd => pd.PlannedMealId)
                .HasConversion(id => id.Value, v => PlannedMealId.From(v))
                .HasColumnName("planned_meal_id")
                .IsRequired();
            b.Property(pd => pd.RecipeId).HasColumnName("recipe_id");
            b.Property(pd => pd.ProductId).HasColumnName("product_id");
            b.Property(pd => pd.Servings).HasColumnName("servings").IsRequired();
            b.Property(pd => pd.Ordinal).HasColumnName("ordinal").IsRequired();

            // UNIQUE (planned_meal_id, ordinal)
            b.HasIndex(pd => new { pd.PlannedMealId, pd.Ordinal })
                .IsUnique()
                .HasDatabaseName("ux_planned_dish_meal_ordinal");

            b.HasQueryFilter(pd => pd.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── MealSlotConfig aggregate root ───────────────────────────────────────
        builder.Entity<MealSlotConfig>(b =>
        {
            b.ToTable("meal_slot_config");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id)
                .HasConversion(id => id.Value, v => MealSlotConfigId.From(v))
                .HasColumnName("meal_slot_config_id")
                .ValueGeneratedNever();
            b.Property(c => c.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(c => c.CreatedAt).HasColumnName("created_at");
            b.Property(c => c.UpdatedAt).HasColumnName("updated_at");

            // UNIQUE (household_id) — one config per household
            b.HasIndex(c => c.HouseholdId)
                .IsUnique()
                .HasDatabaseName("ux_meal_slot_config_household");

            // UNIQUE (household_id, meal_slot_config_id) — anchor for composite child FKs
            b.HasIndex(c => new { c.HouseholdId, c.Id })
                .IsUnique()
                .HasDatabaseName("ux_meal_slot_config_household_id");

            b.HasMany(c => c.Slots)
                .WithOne()
                .HasForeignKey(s => s.ConfigId)
                .HasPrincipalKey(c => c.Id)
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(c => c.Slots)
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasField("_slots");

            b.HasQueryFilter(c => c.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── MealSlot ────────────────────────────────────────────────────────────
        builder.Entity<MealSlot>(b =>
        {
            b.ToTable("meal_slot");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id)
                .HasConversion(id => id.Value, v => MealSlotId.From(v))
                .HasColumnName("meal_slot_id")
                .ValueGeneratedNever();
            b.Property(s => s.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(s => s.ConfigId)
                .HasConversion(id => id.Value, v => MealSlotConfigId.From(v))
                .HasColumnName("meal_slot_config_id")
                .IsRequired();
            b.Property(s => s.Label).HasColumnName("label").IsRequired();
            b.Property(s => s.Ordinal).HasColumnName("ordinal").IsRequired();
            b.Property(s => s.DefaultAttendees)
                .HasColumnName("default_attendees")
                .HasColumnType("uuid[]")
                .HasDefaultValueSql("'{}'::uuid[]");
            b.Property(s => s.ArchivedAt).HasColumnName("archived_at");

            // UNIQUE (household_id, meal_slot_id) — anchor for planned_meal composite FK
            b.HasIndex(s => new { s.HouseholdId, s.Id })
                .IsUnique()
                .HasDatabaseName("ux_meal_slot_household_id");

            b.HasQueryFilter(s => s.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── UserPreference aggregate root ───────────────────────────────────────
        builder.Entity<UserPreference>(b =>
        {
            b.ToTable("user_preference");
            b.HasKey(up => up.Id);
            b.Property(up => up.Id)
                .HasConversion(id => id.Value, v => UserPreferenceId.From(v))
                .HasColumnName("user_preference_id")
                .ValueGeneratedNever();
            b.Property(up => up.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(up => up.UserId).HasColumnName("user_id").IsRequired();
            b.Property(up => up.CreatedAt).HasColumnName("created_at");
            b.Property(up => up.UpdatedAt).HasColumnName("updated_at");

            // UNIQUE (household_id, user_preference_id) — anchor for composite child FKs
            b.HasIndex(up => new { up.HouseholdId, up.Id })
                .IsUnique()
                .HasDatabaseName("ux_user_preference_household_id");

            // UNIQUE (household_id, user_id) — one profile per member (M6)
            b.HasIndex(up => new { up.HouseholdId, up.UserId })
                .IsUnique()
                .HasDatabaseName("ux_user_preference_household_user");

            b.HasMany(up => up.TagStances)
                .WithOne()
                .HasForeignKey(ts => ts.UserPreferenceId)
                .HasPrincipalKey(up => up.Id)
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(up => up.TagStances)
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasField("_tagStances");

            b.HasQueryFilter(up => up.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── TagStance ───────────────────────────────────────────────────────────
        builder.Entity<TagStance>(b =>
        {
            b.ToTable("tag_stance");
            b.HasKey(ts => ts.Id);
            b.Property(ts => ts.Id)
                .HasConversion(id => id.Value, v => TagStanceId.From(v))
                .HasColumnName("tag_stance_id")
                .ValueGeneratedNever();
            b.Property(ts => ts.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(ts => ts.UserPreferenceId)
                .HasConversion(id => id.Value, v => UserPreferenceId.From(v))
                .HasColumnName("user_preference_id")
                .IsRequired();
            b.Property(ts => ts.TagId).HasColumnName("tag_id").IsRequired();
            b.Property(ts => ts.Stance).HasColumnName("stance").IsRequired();

            // UNIQUE (user_preference_id, tag_id) — one stance per tag (M6)
            b.HasIndex(ts => new { ts.UserPreferenceId, ts.TagId })
                .IsUnique()
                .HasDatabaseName("ux_tag_stance_pref_tag");

            b.HasQueryFilter(ts => ts.HouseholdId == HouseholdId.From(_householdId));
        });
    }

    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}
