using Microsoft.EntityFrameworkCore;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;

namespace Plantry.Planning.Infrastructure;

/// <summary>
/// The single EF DbContext for the Planning bounded context (ADR-024 Phase A). Unifies what were
/// formerly two separate DbContexts — <c>MealPlanningDbContext</c> and <c>ShoppingDbContext</c> — kept
/// apart only during the interim (plantry-g3da.5) merge to avoid touching migration history. This
/// context owns both physical schemas unchanged (plantry-g3da.8 does not move data — see ADR-024
/// §"Physical schemas do not move on day one"):
/// <list type="bullet">
/// <item><b>meal_planning</b> — five aggregates: <see cref="MealPlan"/> (+ <see cref="PlannedMeal"/> +
/// <see cref="PlannedDish"/> children), <see cref="MealSlotConfig"/> (+ <see cref="MealSlot"/>
/// children), <see cref="UserPreference"/> (+ <see cref="TagStance"/> children),
/// <see cref="HouseholdPlanningSettings"/>, and <see cref="WeekPlanningOverride"/>.</item>
/// <item><b>shopping</b> — <see cref="ShoppingList"/> aggregate root + <see cref="ShoppingListItem"/>
/// children + <see cref="ShoppingListItemContribution"/> grandchildren (plantry-9scq). Mutable working
/// state (not append-only): items edited in place, hard-deleted on clear (shopping.md).</item>
/// </list>
/// The EF migrations-history table (<c>__EFMigrationsHistory</c>) lives in the <c>shopping</c> schema —
/// this context's default schema — reusing the location the old <c>ShoppingDbContext</c> already used,
/// rather than introducing a third schema purely for bookkeeping.
/// <para>
/// The RlsMiddleware MUST call <see cref="SetHouseholdId"/> on this context for every authenticated
/// request, exactly as for the other bounded-context DbContexts (the known P2-0/P3-0 gotcha: omitting
/// it leaves _householdId as Guid.Empty and every EF query filter returns nothing).
/// </para>
/// </summary>
public sealed class PlanningDbContext(DbContextOptions<PlanningDbContext> options) : DbContext(options)
{
    // ── Meal Planning ────────────────────────────────────────────────────────
    public DbSet<MealPlan> MealPlans => Set<MealPlan>();
    public DbSet<PlannedMeal> PlannedMeals => Set<PlannedMeal>();
    public DbSet<PlannedDish> PlannedDishes => Set<PlannedDish>();
    public DbSet<MealSlotConfig> MealSlotConfigs => Set<MealSlotConfig>();
    public DbSet<MealSlot> MealSlots => Set<MealSlot>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
    public DbSet<TagStance> TagStances => Set<TagStance>();
    public DbSet<HouseholdPlanningSettings> HouseholdPlanningSettings => Set<HouseholdPlanningSettings>();
    public DbSet<WeekPlanningOverride> WeekPlanningOverrides => Set<WeekPlanningOverride>();

    // ── Shopping ─────────────────────────────────────────────────────────────
    public DbSet<ShoppingList> ShoppingLists => Set<ShoppingList>();
    public DbSet<ShoppingListItem> ShoppingListItems => Set<ShoppingListItem>();
    public DbSet<ShoppingListItemContribution> ShoppingListItemContributions => Set<ShoppingListItemContribution>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Default schema covers Shopping; Meal Planning entities are schema-qualified explicitly below —
        // there is no single default schema for a context spanning two physical schemas.
        builder.HasDefaultSchema("shopping");

        // ── MealPlan aggregate root (meal_planning schema) ──────────────────────
        builder.Entity<MealPlan>(b =>
        {
            b.ToTable("meal_plan", "meal_planning");
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

        // ── PlannedMeal (meal_planning schema) ──────────────────────────────────
        builder.Entity<PlannedMeal>(b =>
        {
            b.ToTable("planned_meal", "meal_planning");
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
            b.Property(pm => pm.Ordinal).HasColumnName("ordinal").IsRequired();
            b.Property(pm => pm.CreatedBy).HasColumnName("created_by").IsRequired();
            b.Property(pm => pm.UpdatedBy).HasColumnName("updated_by").IsRequired();
            b.Property(pm => pm.CreatedAt).HasColumnName("created_at");
            b.Property(pm => pm.UpdatedAt).HasColumnName("updated_at");

            // UNIQUE (household_id, planned_meal_id) — composite FK anchor
            b.HasIndex(pm => new { pm.HouseholdId, pm.Id })
                .IsUnique()
                .HasDatabaseName("ux_planned_meal_household_id");

            // UNIQUE (meal_plan_id, date, meal_slot_id, ordinal) — one meal per position per cell (MP-O8)
            b.HasIndex(pm => new { pm.MealPlanId, pm.Date, pm.MealSlotId, pm.Ordinal })
                .IsUnique()
                .HasDatabaseName("ux_planned_meal_plan_date_slot_ordinal");

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

        // ── PlannedDish (meal_planning schema) ──────────────────────────────────
        builder.Entity<PlannedDish>(b =>
        {
            b.ToTable("planned_dish", "meal_planning", t => t.HasCheckConstraint(
                "ck_planned_dish_shape",
                "((recipe_id IS NOT NULL AND product_id IS NULL AND servings IS NOT NULL AND servings >= 1 AND quantity IS NULL AND unit_id IS NULL) OR " +
                "(recipe_id IS NULL AND product_id IS NOT NULL AND servings IS NULL AND quantity IS NOT NULL AND quantity > 0 AND unit_id IS NOT NULL AND unit_id <> '00000000-0000-0000-0000-000000000000'))"));
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
            b.Property(pd => pd.Servings).HasColumnName("servings");
            b.Property(pd => pd.Quantity).HasColumnName("quantity").HasPrecision(12, 3);
            b.Property(pd => pd.UnitId).HasColumnName("unit_id");
            b.Property(pd => pd.Ordinal).HasColumnName("ordinal").IsRequired();

            // UNIQUE (planned_meal_id, ordinal)
            b.HasIndex(pd => new { pd.PlannedMealId, pd.Ordinal })
                .IsUnique()
                .HasDatabaseName("ux_planned_dish_meal_ordinal");

            b.HasQueryFilter(pd => pd.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── MealSlotConfig aggregate root (meal_planning schema) ────────────────
        builder.Entity<MealSlotConfig>(b =>
        {
            b.ToTable("meal_slot_config", "meal_planning");
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

        // ── MealSlot (meal_planning schema) ──────────────────────────────────────
        builder.Entity<MealSlot>(b =>
        {
            b.ToTable("meal_slot", "meal_planning");
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
            b.Property(s => s.IncludeInAutoPlan)
                .HasColumnName("include_in_auto_plan")
                .IsRequired()
                .HasDefaultValue(true);
            b.Property(s => s.ArchivedAt).HasColumnName("archived_at");

            // UNIQUE (household_id, meal_slot_id) — anchor for planned_meal composite FK
            b.HasIndex(s => new { s.HouseholdId, s.Id })
                .IsUnique()
                .HasDatabaseName("ux_meal_slot_household_id");

            b.HasQueryFilter(s => s.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── UserPreference aggregate root (meal_planning schema) ────────────────
        builder.Entity<UserPreference>(b =>
        {
            b.ToTable("user_preference", "meal_planning");
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

        // ── TagStance (meal_planning schema) ─────────────────────────────────────
        builder.Entity<TagStance>(b =>
        {
            b.ToTable("tag_stance", "meal_planning");
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

        // ── HouseholdPlanningSettings aggregate root (meal_planning schema) ─────
        // One row per household. Seeded lazily on first write (null = no target).
        // HouseholdId is both the PK and the aggregate identity (mirrors the pattern used
        // by single-per-household aggregates in this context).
        builder.Entity<HouseholdPlanningSettings>(b =>
        {
            b.ToTable("household_planning_settings", "meal_planning");
            b.HasKey(s => s.HouseholdId);
            b.Property(s => s.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .ValueGeneratedNever();

            // Money stored as two nullable columns: minor_units (bigint) + currency (char(3)).
            // Owned entity mapping splits Money into the parent table (no join table needed).
            b.OwnsOne(s => s.DefaultWeeklyBudget, mb =>
            {
                mb.Property(m => m.MinorUnits).HasColumnName("budget_minor_units");
                mb.Property(m => m.Currency).HasColumnName("budget_currency").HasMaxLength(3);
            });

            // PlanningWeights stored as three nullable int columns.
            b.OwnsOne(s => s.DefaultPlanningWeights, wb =>
            {
                wb.Property(w => w.Waste).HasColumnName("weights_waste");
                wb.Property(w => w.Cost).HasColumnName("weights_cost");
                wb.Property(w => w.Variety).HasColumnName("weights_variety");
            });

            b.HasQueryFilter(s => s.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── WeekPlanningOverride (meal_planning schema) ─────────────────────────
        // One row per (household, weekStart). A row exists only when the user has
        // overridden something for that specific week.
        builder.Entity<WeekPlanningOverride>(b =>
        {
            b.ToTable("week_planning_override", "meal_planning");
            b.HasKey(o => new { HouseholdId = o.HouseholdId, WeekStart = o.WeekStart });
            b.Property(o => o.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id");
            b.Property(o => o.WeekStart).HasColumnName("week_start");

            b.OwnsOne(o => o.BudgetOverride, mb =>
            {
                mb.Property(m => m.MinorUnits).HasColumnName("budget_minor_units");
                mb.Property(m => m.Currency).HasColumnName("budget_currency").HasMaxLength(3);
            });

            b.OwnsOne(o => o.WeightsOverride, wb =>
            {
                wb.Property(w => w.Waste).HasColumnName("weights_waste");
                wb.Property(w => w.Cost).HasColumnName("weights_cost");
                wb.Property(w => w.Variety).HasColumnName("weights_variety");
            });

            b.HasQueryFilter(o => o.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── ShoppingList aggregate root (shopping schema) ───────────────────────
        builder.Entity<ShoppingList>(b =>
        {
            b.ToTable("shopping_list");
            b.HasKey(l => l.Id);
            b.Property(l => l.Id)
                .HasConversion(id => id.Value, v => ShoppingListId.From(v))
                .HasColumnName("shopping_list_id")
                .ValueGeneratedNever();
            b.Property(l => l.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(l => l.Name)
                .HasColumnName("name")
                .IsRequired();
            b.Property(l => l.CreatedAt).HasColumnName("created_at");
            b.Property(l => l.UpdatedAt).HasColumnName("updated_at");

            // Child item collection — backed by _items field (mirrors IntakeDbContext / RecipesDbContext pattern).
            b.HasMany(l => l.Items)
                .WithOne()
                .HasForeignKey(i => i.ShoppingListId)
                .HasPrincipalKey(l => l.Id)
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(l => l.Items)
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasField("_items");

            // Composite UNIQUE so child FK can reference (household_id, shopping_list_id) — per G6-2 convention.
            b.HasIndex(l => new { l.HouseholdId, l.Id })
                .IsUnique()
                .HasDatabaseName("uq_shopping_list_household_list");

            b.HasQueryFilter(l => l.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── ShoppingListItem (shopping schema) ──────────────────────────────────
        builder.Entity<ShoppingListItem>(b =>
        {
            b.ToTable("shopping_list_item");
            b.HasKey(i => i.Id);
            b.Property(i => i.Id)
                .HasConversion(id => id.Value, v => ShoppingListItemId.From(v))
                .HasColumnName("shopping_list_item_id")
                .ValueGeneratedNever();
            b.Property(i => i.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(i => i.ShoppingListId)
                .HasConversion(id => id.Value, v => ShoppingListId.From(v))
                .HasColumnName("shopping_list_id")
                .IsRequired();
            b.Property(i => i.ProductId).HasColumnName("product_id");
            b.Property(i => i.FreeText).HasColumnName("free_text");
            // Quantity is derived (SUM of contributions) — not stored on the item row.
            b.Ignore(i => i.Quantity);
            b.Property(i => i.UnitId).HasColumnName("unit_id");
            b.Property(i => i.CategoryId).HasColumnName("category_id");
            b.Property(i => i.Note).HasColumnName("note");
            b.Property(i => i.CheckedAt).HasColumnName("checked_at");
            b.Property(i => i.CheckedBy).HasColumnName("checked_by");
            // source and source_ref have moved to shopping_list_item_contribution (plantry-9scq).
            b.Property(i => i.CreatedAt).HasColumnName("created_at");
            b.Property(i => i.UpdatedAt).HasColumnName("updated_at");

            // Contribution grandchildren — cascade-delete with parent item.
            b.HasMany(i => i.Contributions)
                .WithOne()
                .HasForeignKey(c => c.ItemId)
                .HasPrincipalKey(i => i.Id)
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(i => i.Contributions)
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasField("_contributions");

            // Backing index for the list view (household_id, shopping_list_id) — shopping.md.
            b.HasIndex(i => new { i.HouseholdId, i.ShoppingListId })
                .HasDatabaseName("ix_shopping_list_item_household_list");

            b.HasQueryFilter(i => i.HouseholdId == HouseholdId.From(_householdId));
        });

        // ── ShoppingListItemContribution (shopping schema) ──────────────────────
        builder.Entity<ShoppingListItemContribution>(b =>
        {
            b.ToTable("shopping_list_item_contribution");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id)
                .HasConversion(id => id.Value, v => ShoppingListItemContributionId.From(v))
                .HasColumnName("contribution_id")
                .ValueGeneratedNever();
            b.Property(c => c.ItemId)
                .HasConversion(id => id.Value, v => ShoppingListItemId.From(v))
                .HasColumnName("shopping_list_item_id")
                .IsRequired();
            b.Property(c => c.Source)
                .HasConversion(s => s.ToDbValue(), v => ItemSourceExtensions.Parse(v))
                .HasColumnName("source")
                .HasMaxLength(20)
                .IsRequired();
            b.Property(c => c.SourceRef).HasColumnName("source_ref");
            b.Property(c => c.Quantity)
                .HasColumnName("quantity")
                .HasPrecision(12, 3);
            b.Property(c => c.UnitId).HasColumnName("unit_id");

            // Index for per-item contribution lookup.
            b.HasIndex(c => c.ItemId)
                .HasDatabaseName("ix_shopping_list_item_contribution_item");
        });
    }

    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}
