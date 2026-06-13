using Microsoft.EntityFrameworkCore;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;

namespace Plantry.Recipes.Infrastructure;

/// <summary>
/// EF DbContext for the Recipes bounded context (<c>recipes</c> schema). Owns six tables: the
/// <c>recipe</c> aggregate root + its <c>recipe_ingredient</c> children, the 1:1 <c>recipe_photo</c>
/// (hot-path separated), and the <c>recipe_tag</c> membership join — plus the standalone <c>tag</c>
/// vocabulary root and the append-only <c>cook_event</c> root.
/// <para>
/// This P2-0 step maps the schema, migration and RLS only; the Recipe aggregate's child-navigation
/// mapping and repository land in P2-1. The tenant-safe composite <c>(household_id, parent_id)</c> FKs,
/// CHECK constraints, and per-table RLS policies are applied in the migration's raw-SQL block (mirroring
/// <c>intake.InitialIntakeSchema</c>) rather than via EF relationships, so this context maps no
/// navigations yet. Each table is independently RLS-filtered on <c>household_id</c>.
/// </para>
/// </summary>
public sealed class RecipesDbContext(DbContextOptions<RecipesDbContext> options) : DbContext(options)
{
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<Ingredient> Ingredients => Set<Ingredient>();
    public DbSet<RecipePhoto> RecipePhotos => Set<RecipePhoto>();
    public DbSet<CookEvent> CookEvents => Set<CookEvent>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<RecipeTag> RecipeTags => Set<RecipeTag>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("recipes");

        builder.Entity<Recipe>(b =>
        {
            b.ToTable("recipe");
            b.HasKey(r => r.Id);
            b.Property(r => r.Id)
                .HasConversion(id => id.Value, v => RecipeId.From(v))
                .HasColumnName("recipe_id")
                .ValueGeneratedNever();
            b.Property(r => r.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(r => r.Name).HasColumnName("name").IsRequired();
            b.Property(r => r.Source).HasColumnName("source");
            b.Property(r => r.CookTimeMinutes).HasColumnName("cook_time_minutes");
            b.Property(r => r.DefaultServings).HasColumnName("default_servings").IsRequired();
            b.Property(r => r.Directions).HasColumnName("directions");
            b.Property(r => r.ArchivedAt).HasColumnName("archived_at");
            b.Property(r => r.CreatedAt).HasColumnName("created_at");
            b.Property(r => r.UpdatedAt).HasColumnName("updated_at");

            // Browse-sort backing indexes (J2): the unique name index serves Name; created_at serves
            // "Recently added"; cook_time_minutes serves Cook time.
            b.HasIndex(r => new { r.HouseholdId, r.Name })
                .IsUnique()
                .HasDatabaseName("ux_recipe_household_name");
            b.HasIndex(r => new { r.HouseholdId, r.CreatedAt })
                .HasDatabaseName("ix_recipe_household_created");
            b.HasIndex(r => new { r.HouseholdId, r.CookTimeMinutes })
                .HasDatabaseName("ix_recipe_household_cook_time");
            b.HasQueryFilter(r => r.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<Ingredient>(b =>
        {
            b.ToTable("recipe_ingredient");
            b.HasKey(i => i.Id);
            b.Property(i => i.Id)
                .HasConversion(id => id.Value, v => IngredientId.From(v))
                .HasColumnName("ingredient_id")
                .ValueGeneratedNever();
            b.Property(i => i.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(i => i.RecipeId)
                .HasConversion(id => id.Value, v => RecipeId.From(v))
                .HasColumnName("recipe_id")
                .IsRequired();
            b.Property(i => i.ProductId).HasColumnName("product_id").IsRequired();
            b.Property(i => i.Quantity).HasColumnName("quantity").HasPrecision(12, 3);
            b.Property(i => i.UnitId).HasColumnName("unit_id");
            b.Property(i => i.GroupHeading).HasColumnName("group_heading");
            b.Property(i => i.Ordinal).HasColumnName("ordinal").IsRequired();

            // Leads with recipe_id, so it also serves the composite-FK lookup back to recipe.
            b.HasIndex(i => new { i.RecipeId, i.Ordinal })
                .IsUnique()
                .HasDatabaseName("ux_recipe_ingredient_recipe_ordinal");
            b.HasQueryFilter(i => i.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<RecipePhoto>(b =>
        {
            b.ToTable("recipe_photo");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id)
                .HasConversion(id => id.Value, v => RecipeId.From(v))
                .HasColumnName("recipe_id")
                .ValueGeneratedNever();
            b.Property(p => p.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(p => p.Content).HasColumnName("content").IsRequired();
            b.Property(p => p.ContentType).HasColumnName("content_type").IsRequired();
            b.Property(p => p.Sha256).HasColumnName("sha256");
            b.Property(p => p.CreatedAt).HasColumnName("created_at");
            b.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            b.HasQueryFilter(p => p.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<CookEvent>(b =>
        {
            b.ToTable("cook_event");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id)
                .HasConversion(id => id.Value, v => CookEventId.From(v))
                .HasColumnName("cook_event_id")
                .ValueGeneratedNever();
            b.Property(c => c.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(c => c.RecipeId)
                .HasConversion(id => id.Value, v => RecipeId.From(v))
                .HasColumnName("recipe_id")
                .IsRequired();
            b.Property(c => c.ServingsCooked).HasColumnName("servings_cooked").IsRequired();
            b.Property(c => c.CookedBy).HasColumnName("cooked_by").IsRequired();
            b.Property(c => c.CookedAt).HasColumnName("cooked_at");

            b.HasIndex(c => new { c.HouseholdId, c.RecipeId, c.CookedAt })
                .HasDatabaseName("ix_cook_event_household_recipe_cooked");
            b.HasQueryFilter(c => c.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<Tag>(b =>
        {
            b.ToTable("tag");
            b.HasKey(t => t.Id);
            b.Property(t => t.Id)
                .HasConversion(id => id.Value, v => TagId.From(v))
                .HasColumnName("tag_id")
                .ValueGeneratedNever();
            b.Property(t => t.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(t => t.Name).HasColumnName("name").IsRequired();
            // Cosmetic enum, nullable — persisted as text + CHECK (applied in the migration's raw SQL).
            b.Property(t => t.Category)
                .HasConversion(
                    c => c == null ? null : c.Value.ToDbValue(),
                    v => v == null ? (TagCategory?)null : TagCategoryExtensions.Parse(v))
                .HasColumnName("category");
            b.Property(t => t.CreatedAt).HasColumnName("created_at");
            b.Property(t => t.UpdatedAt).HasColumnName("updated_at");

            b.HasIndex(t => new { t.HouseholdId, t.Name })
                .IsUnique()
                .HasDatabaseName("ux_tag_household_name");
            b.HasQueryFilter(t => t.HouseholdId == HouseholdId.From(_householdId));
        });

        builder.Entity<RecipeTag>(b =>
        {
            b.ToTable("recipe_tag");
            b.HasKey(rt => new { rt.RecipeId, rt.TagId });
            b.Property(rt => rt.HouseholdId)
                .HasConversion(id => id.Value, v => HouseholdId.From(v))
                .HasColumnName("household_id")
                .IsRequired();
            b.Property(rt => rt.RecipeId)
                .HasConversion(id => id.Value, v => RecipeId.From(v))
                .HasColumnName("recipe_id");
            b.Property(rt => rt.TagId)
                .HasConversion(id => id.Value, v => TagId.From(v))
                .HasColumnName("tag_id");

            // Reverse index powers "filter recipes by tag" (J2).
            b.HasIndex(rt => new { rt.HouseholdId, rt.TagId })
                .HasDatabaseName("ix_recipe_tag_household_tag");
            b.HasQueryFilter(rt => rt.HouseholdId == HouseholdId.From(_householdId));
        });
    }

    private Guid _householdId;
    public void SetHouseholdId(Guid id) => _householdId = id;
}
