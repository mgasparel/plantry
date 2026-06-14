using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Recipes.Application;

/// <summary>
/// Application service that drives recipe authoring — Create (J6) and Edit (J7) — assembling validated
/// <see cref="IngredientLine"/>s through the Catalog anti-corruption ports (§8) before calling the pure
/// <see cref="Recipe"/> aggregate. It is the only place that knows about Catalog: the aggregate stays
/// free of cross-context reads/writes (recipes-domain-model.md §3 note, §7).
///
/// <para>Per line it resolves the typed product (search/select <c>ProductId</c>, or inline-create an
/// untracked staple via <see cref="ICatalogWriter"/>, C12); enforces the narrow R5 rule (null qty/unit
/// only for an untracked product); and validates the unit→product-default conversion path via
/// <see cref="IUnitConverter"/> (R7/C10), surfacing the inline <c>ProductConversion</c> form as
/// <see cref="AuthorRecipeResult.NeedsConversion"/> when no path exists and writing the author-supplied
/// factor on the retry. It mints unknown tag names (J6) before <c>Recipe.SetTags</c>, enforces name
/// uniqueness (R1), and persists through <see cref="IRecipeRepository"/> — RecipeCreated/RecipeUpdated
/// flow out through the DomainEventDispatch interceptor on save.</para>
/// </summary>
public sealed class AuthorRecipe(
    IRecipeRepository recipes,
    ITagRepository tags,
    ICatalogProductReader products,
    ICatalogWriter catalogWriter,
    IUnitConverter unitConverter,
    IClock clock,
    ITenantContext tenant)
{
    public async Task<AuthorRecipeResult> ExecuteAsync(AuthorRecipeCommand command, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            return new AuthorRecipeResult.Invalid(Error.Unauthorized);
        var household = HouseholdId.From(householdGuid);

        // Edit loads the existing aggregate up front so name-uniqueness can ignore its own current name.
        Recipe? existing = null;
        if (command.RecipeId is { } editId)
        {
            existing = await recipes.GetByIdAsync(editId, ct);
            if (existing is null)
                return new AuthorRecipeResult.Invalid(Error.NotFound);
        }

        var name = command.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name))
            return new AuthorRecipeResult.Invalid(Error.Custom("Recipes.InvalidName", "Recipe name must not be blank."));

        // R1 — name unique per household. On edit, only check when the name actually changes; otherwise
        // the recipe's own row makes the lookup a false positive.
        var nameChanged = existing is null || !string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase);
        if (nameChanged && await recipes.NameExistsAsync(household, name, ct))
            return new AuthorRecipeResult.Invalid(
                Error.Custom("Recipes.DuplicateName", $"A recipe named '{name}' already exists."));

        // ── Per-line product resolution (search/select or inline untracked-staple create, C12) ──
        var resolved = new List<ResolvedLine>(command.Lines.Count);
        foreach (var line in command.Lines)
        {
            if (line.ProductId is { } chosenId)
            {
                var product = await products.FindAsync(chosenId, ct);
                if (product is null)
                    return new AuthorRecipeResult.Invalid(
                        Error.Custom("Recipes.UnknownProduct", "A chosen ingredient product does not exist."));
                resolved.Add(new ResolvedLine(product.Id, product.TrackStock, product.DefaultUnitId, line));
            }
            else if (!string.IsNullOrWhiteSpace(line.NewStapleName))
            {
                if (line.NewStapleDefaultUnitId is not { } stapleUnit)
                    return new AuthorRecipeResult.Invalid(
                        Error.Custom("Recipes.MissingStapleUnit", "An inline staple needs a default unit."));
                var newId = await catalogWriter.CreateUntrackedStapleAsync(line.NewStapleName.Trim(), stapleUnit, ct);
                // An inline staple is untracked (track_stock = false) by construction (C12).
                resolved.Add(new ResolvedLine(newId, TrackStock: false, stapleUnit, line));
            }
            else
            {
                return new AuthorRecipeResult.Invalid(
                    Error.Custom("Recipes.LineMissingProduct", "Each ingredient must choose a product or name a new staple."));
            }
        }

        // ── R5 — a null Quantity/UnitId is permitted ONLY for an untracked product ──
        foreach (var r in resolved)
        {
            if (r.TrackStock && (r.Line.Quantity is null || r.Line.UnitId is null))
                return new AuthorRecipeResult.Invalid(Error.Custom(
                    "Recipes.TrackedRequiresQuantity",
                    "A tracked ingredient must have both a quantity and a unit."));
        }

        // ── R7/C10 — unit→product-default conversion path for each tracked line ──
        // Apply any author-supplied factors first so a just-written conversion resolves on this same pass,
        // then collect the lines that still have no path. Save is blocked while any remain.
        foreach (var r in resolved)
        {
            if (NeedsConversionCheck(r) && r.Line.ConversionFactor is { } factor)
                await catalogWriter.AddConversionAsync(r.ProductId, r.Line.UnitId!.Value, r.DefaultUnitId, factor, ct);
        }

        var missing = new List<ConversionNeeded>();
        foreach (var r in resolved)
        {
            if (!NeedsConversionCheck(r))
                continue;
            var path = await unitConverter.ConvertAsync(r.ProductId, 1m, r.Line.UnitId!.Value, r.DefaultUnitId, ct);
            if (path.IsFailure)
                missing.Add(new ConversionNeeded(r.Line.Ordinal, r.ProductId, r.Line.UnitId!.Value, r.DefaultUnitId));
        }

        if (missing.Count > 0)
            return new AuthorRecipeResult.NeedsConversion(missing);

        // ── Inline tag minting (J6) — resolve each typed name to an existing or freshly minted Tag ──
        var tagIds = new List<TagId>();
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawTag in command.TagNames)
        {
            var tagName = rawTag?.Trim();
            if (string.IsNullOrEmpty(tagName) || !seenTags.Add(tagName))
                continue;

            var tag = await tags.FindByNameAsync(household, tagName, ct);
            if (tag is null)
            {
                tag = Tag.Create(household, tagName, category: null, clock);
                await tags.AddAsync(tag, ct);
            }
            tagIds.Add(tag.Id);
        }

        // ── Assemble the ordered, validated lines with contiguous 0-based ordinals ──
        var domainLines = resolved
            .OrderBy(r => r.Line.Ordinal)
            .Select((r, index) => new IngredientLine(
                r.ProductId, r.Line.Quantity, r.Line.UnitId, r.Line.GroupHeading, index))
            .ToList();

        // ── Create or edit the aggregate ──
        Recipe recipe;
        if (existing is null)
        {
            var created = Recipe.Create(household, name, command.DefaultServings, clock);
            if (created.IsFailure)
                return new AuthorRecipeResult.Invalid(created.Error);
            recipe = created.Value;
            ApplyScalars(recipe, command);

            var replace = recipe.ReplaceIngredients(domainLines, clock);
            if (replace.IsFailure)
                return new AuthorRecipeResult.Invalid(replace.Error);

            recipe.SetTags(tagIds, clock);
            await recipes.AddAsync(recipe, ct);
        }
        else
        {
            recipe = existing;
            var rename = recipe.Rename(name, clock);
            if (rename.IsFailure)
                return new AuthorRecipeResult.Invalid(rename.Error);
            ApplyScalars(recipe, command);

            var replace = recipe.ReplaceIngredients(domainLines, clock);
            if (replace.IsFailure)
                return new AuthorRecipeResult.Invalid(replace.Error);

            // J7 step 3 — a servings change scales (Proportional) or preserves (Keep) the just-set lines.
            if (command.DefaultServings != recipe.DefaultServings)
            {
                var change = recipe.ChangeDefaultServings(command.DefaultServings, command.ScaleMode, clock);
                if (change.IsFailure)
                    return new AuthorRecipeResult.Invalid(change.Error);
            }

            recipe.SetTags(tagIds, clock);
        }

        await recipes.SaveChangesAsync(ct);
        return new AuthorRecipeResult.Saved(recipe.Id);
    }

    private void ApplyScalars(Recipe recipe, AuthorRecipeCommand command)
    {
        recipe.SetSource(command.Source, clock);
        recipe.SetCookTime(command.CookTimeMinutes, clock);
        recipe.SetDirections(command.Directions, clock);
    }

    /// <summary>A tracked line whose unit differs from the product default needs a conversion path (R7).</summary>
    private static bool NeedsConversionCheck(ResolvedLine r) =>
        r.TrackStock && r.Line.UnitId is { } unit && unit != r.DefaultUnitId;

    private readonly record struct ResolvedLine(Guid ProductId, bool TrackStock, Guid DefaultUnitId, AuthorIngredientLine Line);
}

// ── Command / DTOs ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Editor input for <see cref="AuthorRecipe"/>. <see cref="RecipeId"/> null ⇒ create (J6); set ⇒ edit
/// (J7). <see cref="ScaleMode"/> applies only on an edit that changes <see cref="DefaultServings"/>.
/// </summary>
public sealed record AuthorRecipeCommand(
    RecipeId? RecipeId,
    string Name,
    int DefaultServings,
    IReadOnlyList<AuthorIngredientLine> Lines,
    IReadOnlyList<string> TagNames,
    string? Source = null,
    int? CookTimeMinutes = null,
    string? Directions = null,
    ScaleMode ScaleMode = ScaleMode.Keep);

/// <summary>
/// One authored ingredient row. Carries <b>either</b> a chosen <see cref="ProductId"/> (search/select)
/// <b>or</b> an inline untracked-staple request (<see cref="NewStapleName"/> +
/// <see cref="NewStapleDefaultUnitId"/>, C12). <see cref="ConversionFactor"/> is the author-supplied
/// factor (from <see cref="UnitId"/> to the product's default unit) returned on the retry after a
/// <see cref="AuthorRecipeResult.NeedsConversion"/> outcome (C10).
/// </summary>
public sealed record AuthorIngredientLine(
    Guid? ProductId,
    decimal? Quantity,
    Guid? UnitId,
    string? GroupHeading,
    int Ordinal,
    string? NewStapleName = null,
    Guid? NewStapleDefaultUnitId = null,
    decimal? ConversionFactor = null);

// ── Result ──────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The outcome of an authoring attempt. <see cref="Saved"/> on success; <see cref="NeedsConversion"/>
/// when one or more tracked lines lack a unit→product conversion path and the editor must surface the
/// inline <c>ProductConversion</c> form (save blocked, R7/C10); <see cref="Invalid"/> for a validation
/// failure carrying the domain <see cref="Error"/>.
/// </summary>
public abstract record AuthorRecipeResult
{
    private AuthorRecipeResult() { }

    public sealed record Saved(RecipeId RecipeId) : AuthorRecipeResult;

    public sealed record NeedsConversion(IReadOnlyList<ConversionNeeded> Conversions) : AuthorRecipeResult;

    public sealed record Invalid(Error Error) : AuthorRecipeResult;
}

/// <summary>
/// A tracked ingredient line whose <see cref="FromUnitId"/> has no conversion path to the product's
/// <see cref="ToUnitId"/> default. The editor renders the inline ProductConversion form for this line
/// (identified by <see cref="Ordinal"/>); on save the author-supplied factor comes back on
/// <see cref="AuthorIngredientLine.ConversionFactor"/>.
/// </summary>
public sealed record ConversionNeeded(int Ordinal, Guid ProductId, Guid FromUnitId, Guid ToUnitId);
