using Microsoft.Extensions.Logging;
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
/// factor on the retry. Tags are resolved by <see cref="TagId"/> — the picker posts known household tag
/// ids; unknown or foreign ids are silently dropped (no minting). Persists through
/// <see cref="IRecipeRepository"/> — RecipeCreated/RecipeUpdated flow out through the DomainEventDispatch
/// interceptor on save.</para>
/// </summary>
public sealed class AuthorRecipe(
    IRecipeRepository recipes,
    ITagRepository tags,
    ICatalogProductReader products,
    ICatalogWriter catalogWriter,
    IUnitConverter unitConverter,
    IClock clock,
    ITenantContext tenant,
    ILogger<AuthorRecipe> logger)
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

        // ── Per-line product resolution (search/select, inline untracked-staple create C12,
        //    or inline tracked-product create plantry-orix) ──
        var resolved = new List<ResolvedLine>(command.Lines.Count);
        foreach (var line in command.Lines)
        {
            if (line.ProductId is { } chosenId)
            {
                var product = await products.FindAsync(chosenId, ct);
                if (product is null)
                    return new AuthorRecipeResult.Invalid(
                        Error.Custom("Recipes.UnknownProduct", "A chosen ingredient product does not exist."));
                resolved.Add(new ResolvedLine(product.Id, product.Name, product.TrackStock, product.DefaultUnitId, line));
            }
            else if (line.NewIsTracked && !string.IsNullOrWhiteSpace(line.NewStapleName))
            {
                // Tracked product create from the create-view (plantry-orix). Three sub-paths:
                //   A. Join existing group  — newGroupId non-empty → CreateVariantCommand
                //   B. Create new group     — newGroupName non-empty, newGroupId empty → CreateGroupedProductCommand
                //   C. Standalone tracked   — both group fields empty → CreateProductCommand (track_stock: true)
                if (line.NewStapleDefaultUnitId is not { } trackedUnit)
                    return new AuthorRecipeResult.Invalid(
                        Error.Custom("Recipes.MissingStapleUnit", "An inline tracked product needs a default unit."));

                // R5 pre-check: a tracked ingredient must have both qty and unitId. We validate BEFORE
                // the Catalog write so no orphan product is minted when the user hasn't set a quantity
                // (the create-view has no qty field; the user must re-open the row in the search view
                // to fill qty). Without this guard the product would be created in Catalog and then the
                // recipe save would still fail, leaving the catalog in a dirty state (plantry-orix).
                if (line.Quantity is null || line.UnitId is null)
                    return new AuthorRecipeResult.Invalid(Error.Custom(
                        "Recipes.TrackedRequiresQuantity",
                        $"'{line.NewStapleName.Trim()}' (line {line.Ordinal + 1}) is tracked and needs a quantity and unit."));

                var trackedName = line.NewStapleName.Trim();
                Guid newTrackedId;

                if (!string.IsNullOrWhiteSpace(line.NewGroupId) && Guid.TryParse(line.NewGroupId, out var parentGroupId))
                {
                    // Path A: create as a variant of an existing group.
                    var unitOverride = trackedUnit == Guid.Empty ? (Guid?)null : trackedUnit;
                    var catOverride  = line.NewStapleCategoryId;
                    newTrackedId = await catalogWriter.CreateTrackedVariantAsync(
                        parentGroupId, trackedName, unitOverride, catOverride, ct);
                }
                else if (!string.IsNullOrWhiteSpace(line.NewGroupName))
                {
                    // Path B: create new group + first variant atomically.
                    newTrackedId = await catalogWriter.CreateTrackedGroupedProductAsync(
                        line.NewGroupName.Trim(), trackedName,
                        trackedUnit, line.NewStapleCategoryId, ct);
                }
                else
                {
                    // Path C: standalone tracked product (no group).
                    newTrackedId = await catalogWriter.CreateTrackedProductAsync(
                        trackedName, trackedUnit, line.NewStapleCategoryId, ct);
                }

                // A freshly created tracked product has trackStock: true; its default unit is the supplied unit.
                resolved.Add(new ResolvedLine(newTrackedId, trackedName, TrackStock: true, trackedUnit, line));
            }
            else if (!string.IsNullOrWhiteSpace(line.NewStapleName))
            {
                // Untracked staple (C12, NewIsTracked = false).
                if (line.NewStapleDefaultUnitId is not { } stapleUnit)
                    return new AuthorRecipeResult.Invalid(
                        Error.Custom("Recipes.MissingStapleUnit", "An inline staple needs a default unit."));
                var newId = await catalogWriter.CreateUntrackedStapleAsync(line.NewStapleName.Trim(), stapleUnit, ct);
                // An inline staple is untracked (track_stock = false) by construction (C12).
                resolved.Add(new ResolvedLine(newId, line.NewStapleName.Trim(), TrackStock: false, stapleUnit, line));
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
                    $"'{r.ProductName}' (line {r.Line.Ordinal + 1}) is tracked and needs a quantity and unit."));
        }

        // ── R7/C10 — unit→product-default conversion path for each tracked line ──
        // Apply any author-supplied factors first so a just-written conversion resolves on this same pass,
        // then collect the lines that still have no path. Save is blocked while any remain.
        foreach (var r in resolved)
        {
            if (NeedsConversionCheck(r) && r.Line.ConversionFactor is { } factor && factor > 0)
            {
                // plantry-qno9: the author can now define the conversion against ANY unit pair
                // ("1 kg = 8 cups"), not just recipeUnit→productDefault. When the line carries an
                // explicit from/to (the four-field in-sheet equation), honour it verbatim; otherwise
                // fall back to the legacy assumption (from = the recipe line unit, to = product default)
                // used by the post-save row-level backstop's single-factor form.
                var fromUnitId = r.Line.ConversionFromUnitId ?? r.Line.UnitId!.Value;
                var toUnitId = r.Line.ConversionToUnitId ?? r.DefaultUnitId;
                if (fromUnitId != toUnitId)
                    await catalogWriter.AddConversionAsync(r.ProductId, fromUnitId, toUnitId, factor, ct);
            }
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

        // A missing conversion is a cross-dimension unit gap (same-dimension pairs resolve via universal
        // factor_to_base and never reach here). By default the save is blocked so the editor can prompt for
        // the factor inline (R7/C10). When the caller opts into deferral (edit-moment AI assistance is on
        // and a conversion seeder is available, plantry-qll2.4) the recipe instead saves WITH the gap and
        // carries the gaps out on Saved, so the caller can seed an ai_suggested factor asynchronously
        // (ADR-022) — the user is never prompted and the save never waits.
        if (missing.Count > 0 && !command.DeferMissingConversions)
        {
            logger.LogWarning(
                "AuthorRecipe for '{RecipeName}' requires {ConversionCount} unit conversion(s) before saving.",
                name, missing.Count);
            return new AuthorRecipeResult.NeedsConversion(missing);
        }

        var deferredConversions = command.DeferMissingConversions
            ? (IReadOnlyList<ConversionNeeded>)missing
            : [];
        if (deferredConversions.Count > 0)
            logger.LogInformation(
                "AuthorRecipe for '{RecipeName}' saving with {ConversionCount} deferred unit gap(s) for async AI seeding.",
                name, deferredConversions.Count);

        // ── Tag resolution — resolve each submitted TagId to an existing household tag; drop unknowns ──
        // The picker posts closed-vocabulary TagIds; no minting occurs. Unknown/foreign ids are dropped
        // silently so a stale client or a tampered request cannot mint or reference tags outside the
        // household (server validates membership; RLS / query filter also applies in GetByIdAsync).
        var tagIds = new List<TagId>();
        var seenTagIds = new HashSet<TagId>();
        foreach (var rawId in command.TagIds)
        {
            var tid = TagId.From(rawId);
            if (!seenTagIds.Add(tid))
                continue;

            var tag = await tags.GetByIdAsync(tid, ct);
            if (tag is not null)
                tagIds.Add(tag.Id);
        }

        // ── Inclusion resolution (recipe-composition.md N4): same-household + sub-existence + DAG ──
        // GetByIdAsync applies the household RLS query filter, so a sub in another household resolves to
        // null (same-household check folds into sub-existence). The DAG cycle walk runs after — and only on
        // an edit, where the owning recipe already exists in the persisted graph (a brand-new recipe cannot
        // be part of a cycle: nothing references it yet).
        var commandInclusions = command.Inclusions ?? [];
        var resolvedInclusions = new List<AuthorInclusionLine>(commandInclusions.Count);
        foreach (var inc in commandInclusions)
        {
            if (inc.SubRecipeId == Guid.Empty)
                return new AuthorRecipeResult.Invalid(
                    Error.Custom("Recipes.InvalidSubRecipe", "Each inclusion must reference a sub-recipe."));

            var subId = RecipeId.From(inc.SubRecipeId);
            if (existing is not null && subId == existing.Id)
                return new AuthorRecipeResult.Invalid(
                    Error.Custom("Recipes.SelfInclusion", "A recipe cannot include itself."));

            var sub = await recipes.GetByIdAsync(subId, ct);
            if (sub is null)
                return new AuthorRecipeResult.Invalid(
                    Error.Custom("Recipes.UnknownSubRecipe", "An included recipe does not exist in this household."));

            resolvedInclusions.Add(inc);
        }

        // N4 — reject cycles. Walk the household's existing inclusion edges forward from each sub; if the
        // recipe being edited is reachable, adding these inclusions would close a cycle (direct A→B→A or
        // transitive A→B→C→A). Only meaningful on an edit (owner already in the graph).
        if (existing is not null && resolvedInclusions.Count > 0)
        {
            var edges = await recipes.ListInclusionEdgesAsync(ct);
            if (CreatesCycle(existing.Id, resolvedInclusions.Select(i => RecipeId.From(i.SubRecipeId)), edges))
                return new AuthorRecipeResult.Invalid(Error.Custom(
                    "Recipes.InclusionCycle",
                    "Including that recipe would create a cycle (a recipe cannot include itself, directly or indirectly)."));
        }

        // ── Assemble the ordered, validated lines with contiguous 0-based ordinals across BOTH line types ──
        // Merge ingredients and inclusions by their author-supplied ordinal (LINQ OrderBy is stable, so ties
        // keep ingredient-before-inclusion order), then re-number the union 0..n-1 so N3 (shared-space
        // contiguity) holds regardless of how the author interleaved the two line types.
        var merged = resolved
            .Select((r, i) => (Ordinal: r.Line.Ordinal, IsInclusion: false, Index: i))
            .Concat(resolvedInclusions.Select((_, i) => (Ordinal: resolvedInclusions[i].Ordinal, IsInclusion: true, Index: i)))
            .OrderBy(x => x.Ordinal)
            .ToList();

        var domainLines = new List<IngredientLine>(resolved.Count);
        var domainInclusions = new List<InclusionLine>(resolvedInclusions.Count);
        for (var pos = 0; pos < merged.Count; pos++)
        {
            var item = merged[pos];
            if (item.IsInclusion)
            {
                var inc = resolvedInclusions[item.Index];
                domainInclusions.Add(new InclusionLine(
                    RecipeId.From(inc.SubRecipeId), inc.Servings, inc.GroupHeading, pos));
            }
            else
            {
                var r = resolved[item.Index];
                domainLines.Add(new IngredientLine(
                    r.ProductId, r.Line.Quantity, r.Line.UnitId, r.Line.GroupHeading, pos));
            }
        }

        // ── Create or edit the aggregate ──
        Recipe recipe;
        if (existing is null)
        {
            var created = Recipe.Create(household, name, command.DefaultServings, clock);
            if (created.IsFailure)
                return new AuthorRecipeResult.Invalid(created.Error);
            recipe = created.Value;
            ApplyScalars(recipe, command);

            var replace = recipe.ReplaceLines(domainLines, domainInclusions, clock);
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

            var replace = recipe.ReplaceLines(domainLines, domainInclusions, clock);
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

        // ── Yield-on-cook declaration (plantry-854a, recipe-composition.md §9) ──
        var yieldError = await ApplyYieldAsync(recipe, command, existing, ct);
        if (yieldError is { } ye)
            return new AuthorRecipeResult.Invalid(ye);

        await recipes.SaveChangesAsync(ct);
        logger.LogInformation(
            "Recipe '{RecipeName}' {Action} with id {RecipeId}.",
            name, existing is null ? "created" : "updated", recipe.Id.Value);
        return new AuthorRecipeResult.Saved(recipe.Id) { DeferredConversions = deferredConversions };
    }

    private void ApplyScalars(Recipe recipe, AuthorRecipeCommand command)
    {
        recipe.SetSource(command.Source, clock);
        recipe.SetCookTime(command.CookTimeMinutes, clock);
        recipe.SetDirections(command.Directions, clock);
    }

    /// <summary>
    /// Applies the yield-on-cook declaration (plantry-854a, recipe-composition.md §9). Clears the yield
    /// when disabled; otherwise validates the unit + positive quantity, resolves the yield product
    /// (explicit choice → the recipe's already-declared product → an existing same-named tracked product
    /// → a freshly auto-created tracked product named after the recipe), and calls <see cref="Recipe.SetYield"/>.
    /// Returns an <see cref="Error"/> to abort the save, or null on success. The existing-product reuse
    /// avoids a duplicate-name Catalog rejection when a yield is re-enabled after being cleared.
    /// </summary>
    private async Task<Error?> ApplyYieldAsync(
        Recipe recipe, AuthorRecipeCommand command, Recipe? existing, CancellationToken ct)
    {
        if (!command.YieldEnabled)
        {
            var cleared = recipe.SetYield(null, null, null, clock);
            return cleared.IsFailure ? cleared.Error : null;
        }

        if (command.YieldUnitId is not { } yieldUnit || yieldUnit == Guid.Empty)
            return Error.Custom("Recipes.MissingYieldUnit", "A yield needs a unit.");
        if (command.YieldQuantity is not { } yieldQty || yieldQty <= 0m)
            return Error.Custom("Recipes.InvalidYieldQuantity", "Yield quantity must be greater than zero.");

        Guid yieldProductId;
        if (command.YieldProductId is { } chosen && chosen != Guid.Empty)
        {
            // Author chose an existing product for the yield.
            var product = await products.FindAsync(chosen, ct);
            if (product is null)
                return Error.Custom("Recipes.UnknownYieldProduct", "The chosen yield product does not exist.");
            yieldProductId = product.Id;
        }
        else if (existing?.YieldProductId is { } prior)
        {
            // Yield was already enabled — reuse the declared product rather than minting a duplicate.
            yieldProductId = prior;
        }
        else
        {
            // First enablement (or re-enablement after clearing) with no explicit product. Reuse an existing
            // tracked product whose name matches the recipe if one exists (so re-enabling never mints a
            // duplicate-name product Catalog would reject), otherwise auto-create one from the recipe name.
            var matches = await products.SearchAsync(recipe.Name, ct);
            var match = matches.FirstOrDefault(m =>
                m.TrackStock && string.Equals(m.Name, recipe.Name, StringComparison.OrdinalIgnoreCase));
            yieldProductId = match?.Id
                ?? await catalogWriter.CreateTrackedProductAsync(recipe.Name, yieldUnit, categoryId: null, ct);
        }

        var set = recipe.SetYield(yieldProductId, yieldQty, yieldUnit, clock);
        return set.IsFailure ? set.Error : null;
    }

    /// <summary>
    /// N4 DAG check — does adding inclusions from <paramref name="ownerId"/> to <paramref name="subIds"/>
    /// close a cycle? Walks the existing household inclusion edges forward from each sub; a cycle exists iff
    /// the owner is reachable (owner ← … ← sub means sub already includes owner transitively). The new edges
    /// are not yet in <paramref name="edges"/>, which is exactly right — we test whether adding them would
    /// create a cycle. Carries a visited set so a pre-existing (validated) DAG never loops.
    /// </summary>
    private static bool CreatesCycle(
        RecipeId ownerId, IEnumerable<RecipeId> subIds, IReadOnlyList<RecipeInclusionEdge> edges)
    {
        var adjacency = edges
            .GroupBy(e => e.ParentId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.SubId).ToList());

        var visited = new HashSet<RecipeId>();
        var stack = new Stack<RecipeId>(subIds);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == ownerId)
                return true;
            if (!visited.Add(current))
                continue;
            if (adjacency.TryGetValue(current, out var children))
                foreach (var child in children)
                    stack.Push(child);
        }
        return false;
    }

    /// <summary>A tracked line whose unit differs from the product default needs a conversion path (R7).</summary>
    private static bool NeedsConversionCheck(ResolvedLine r) =>
        r.TrackStock && r.Line.UnitId is { } unit && unit != r.DefaultUnitId;

    private readonly record struct ResolvedLine(Guid ProductId, string ProductName, bool TrackStock, Guid DefaultUnitId, AuthorIngredientLine Line);
}

// ── Command / DTOs ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Editor input for <see cref="AuthorRecipe"/>. <see cref="RecipeId"/> null ⇒ create (J6); set ⇒ edit
/// (J7). <see cref="ScaleMode"/> applies only on an edit that changes <see cref="DefaultServings"/>.
/// <see cref="TagIds"/> are household tag ids submitted by the closed-vocabulary picker; unknown/foreign
/// ids are dropped silently (server validates; no minting).
/// </summary>
/// <param name="YieldEnabled">
/// Yield-on-cook (plantry-854a, recipe-composition.md §9): true when the author declares a yield.
/// When false the recipe's yield is cleared. When true, <paramref name="YieldQuantity"/> (&gt; 0) and
/// <paramref name="YieldUnitId"/> are required; the yield product is <paramref name="YieldProductId"/>
/// when the author chose an existing product, otherwise auto-created from the recipe name (reusing an
/// existing same-named tracked product if one already exists, so re-enabling never mints a duplicate).
/// </param>
/// <param name="YieldProductId">Chosen existing yield product; null ⇒ auto-create from the recipe name.</param>
/// <param name="YieldQuantity">Declared yield quantity for the default servings (&gt; 0 when enabled).</param>
/// <param name="YieldUnitId">Unit of the yield quantity — a servings-like count unit by default.</param>
public sealed record AuthorRecipeCommand(
    RecipeId? RecipeId,
    string Name,
    int DefaultServings,
    IReadOnlyList<AuthorIngredientLine> Lines,
    IReadOnlyList<Guid> TagIds,
    string? Source = null,
    int? CookTimeMinutes = null,
    string? Directions = null,
    ScaleMode ScaleMode = ScaleMode.Keep,
    bool DeferMissingConversions = false,
    IReadOnlyList<AuthorInclusionLine>? Inclusions = null,
    bool YieldEnabled = false,
    Guid? YieldProductId = null,
    decimal? YieldQuantity = null,
    Guid? YieldUnitId = null);

/// <summary>
/// One authored inclusion row (recipe-composition.md §3 / D1): "<see cref="Servings"/> servings of
/// <see cref="SubRecipeId"/>". <see cref="Ordinal"/> is in the SHARED ordinal space with
/// <see cref="AuthorIngredientLine"/> — the service canonicalises the union of both line types to a
/// contiguous 0-based sequence, preserving the author's relative order. Same-household reference,
/// sub-existence, and the DAG check (N4) are enforced by <see cref="AuthorRecipe"/> before the aggregate
/// call; N1 (Servings &gt; 0) and N2 (no self-inclusion) are enforced by <see cref="Recipe.ReplaceLines"/>.
/// </summary>
public sealed record AuthorInclusionLine(
    Guid SubRecipeId,
    decimal Servings,
    string? GroupHeading,
    int Ordinal);

/// <summary>
/// One authored ingredient row. Carries <b>either</b> a chosen <see cref="ProductId"/> (search/select)
/// <b>or</b> an inline create request via the create-view. Two inline-create flavours:
/// <list type="bullet">
///   <item>
///     <description>
///       <b>Untracked staple</b> (C12): <see cref="NewIsTracked"/> = false. <see cref="NewStapleName"/>
///       + <see cref="NewStapleDefaultUnitId"/> are required. Creates a <c>track_stock = false</c> product.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Tracked product</b> (plantry-orix): <see cref="NewIsTracked"/> = true.
///       <see cref="NewStapleName"/> + <see cref="NewStapleDefaultUnitId"/> are required.
///       Three sub-paths driven by <see cref="NewGroupId"/> / <see cref="NewGroupName"/>:
///       (A) <see cref="NewGroupId"/> non-empty → join existing group (CreateVariantCommand);
///       (B) <see cref="NewGroupName"/> non-empty → new group + first variant (CreateGroupedProductCommand);
///       (C) both empty → standalone tracked product (CreateProductCommand, track_stock = true).
///       <see cref="NewStapleCategoryId"/> is the optional category Guid supplied from the Defaults collapsible.
///     </description>
///   </item>
/// </list>
/// <see cref="ConversionFactor"/> is the author-supplied factor written to Catalog when a unit→product
/// conversion path is missing (C10). <see cref="ConversionFromUnitId"/> / <see cref="ConversionToUnitId"/>
/// carry the explicit unit pair the author defined the conversion against (plantry-qno9 four-field
/// equation — e.g. from = kg, to = cup for "1 kg = 8 cups"); when null the service falls back to the
/// legacy assumption (from = <see cref="UnitId"/>, to = product default) used by the single-factor
/// post-save backstop.
/// </summary>
public sealed record AuthorIngredientLine(
    Guid? ProductId,
    decimal? Quantity,
    Guid? UnitId,
    string? GroupHeading,
    int Ordinal,
    string? NewStapleName = null,
    Guid? NewStapleDefaultUnitId = null,
    decimal? ConversionFactor = null,
    bool NewIsTracked = false,
    string? NewGroupId = null,
    string? NewGroupName = null,
    Guid? NewStapleCategoryId = null,
    Guid? ConversionFromUnitId = null,
    Guid? ConversionToUnitId = null);

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

    /// <summary>
    /// The recipe was saved. <see cref="DeferredConversions"/> is empty on the normal path; when the
    /// command set <see cref="AuthorRecipeCommand.DeferMissingConversions"/> it lists the cross-dimension
    /// unit gaps the recipe saved <b>with</b> (instead of blocking on <see cref="NeedsConversion"/>), so
    /// the caller can seed an <c>ai_suggested</c> factor for each asynchronously (plantry-qll2.4 / ADR-022).
    /// </summary>
    public sealed record Saved(RecipeId RecipeId) : AuthorRecipeResult
    {
        public IReadOnlyList<ConversionNeeded> DeferredConversions { get; init; } = [];
    }

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
