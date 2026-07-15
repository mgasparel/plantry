using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;

namespace Plantry.Tests.Unit.Recipes.Domain;

/// <summary>
/// L1 domain tests for <see cref="CookConsumePlanner"/> — the pure cook consume-planning rule matrix
/// (C7 default auto-selection, C9 per-line skip, C11 variant split/swap, C12 untracked/unknown skip,
/// D7 whole-inclusion skip, D8 provenance) plus ad-hoc materialization (plantry-7zjm). These exercise the
/// rules directly, with no ports and no mocks — the whole point of the extraction (plantry-wrk4): the
/// matrix was previously testable only through the full <see cref="CookRecipe"/> orchestrator with eight
/// fakes.
/// </summary>
public sealed class CookConsumePlannerTests
{
    // ── Builders ─────────────────────────────────────────────────────────────────

    private static ExpandedLine Line(
        Guid productId,
        Guid unitId,
        decimal? quantity = 100m,
        IReadOnlyList<InclusionId>? path = null,
        Guid? ingredientId = null,
        Guid? sourceRecipeId = null) =>
        new(
            Path: path ?? [],
            IngredientId: IngredientId.From(ingredientId ?? Guid.NewGuid()),
            SourceRecipeId: RecipeId.From(sourceRecipeId ?? Guid.NewGuid()),
            ProductId: productId,
            Quantity: quantity,
            UnitId: quantity is null ? null : unitId,
            GroupPath: []);

    private static Dictionary<Guid, CatalogProductSummary> Catalog(params (Guid Id, bool Tracked)[] entries)
    {
        var d = new Dictionary<Guid, CatalogProductSummary>();
        foreach (var (id, tracked) in entries)
            d[id] = new CatalogProductSummary(id, "Product " + id.ToString()[..8], tracked);
        return d;
    }

    // ── C7: default auto-selection ────────────────────────────────────────────────

    [Fact]
    public void C7_Default_AutoSelection_Uses_Own_Product_At_Scaled_Quantity()
    {
        var product = Guid.NewGuid();
        var unit = Guid.NewGuid();
        var line = Line(product, unit, quantity: 200m);

        var targets = CookConsumePlanner.Plan(
            [line], resolutions: [], adHocLines: [], Catalog((product, true)), scale: 2m);

        var target = Assert.Single(targets);
        Assert.Equal(product, target.ProductId);
        Assert.Equal(400m, target.Quantity); // 200 × scale 2
        Assert.Equal(unit, target.UnitId);
        Assert.Equal(line.IngredientId, target.IngredientId);
    }

    [Fact]
    public void C7_Resolution_With_No_Allocations_And_Not_Skipped_Falls_Through_To_Default()
    {
        var product = Guid.NewGuid();
        var unit = Guid.NewGuid();
        var ingredient = Guid.NewGuid();
        var line = Line(product, unit, quantity: 100m, ingredientId: ingredient);
        var resolution = new IngredientResolution(IngredientId.From(ingredient), IsSkipped: false, Allocations: []);

        var targets = CookConsumePlanner.Plan(
            [line], [resolution], adHocLines: [], Catalog((product, true)), scale: 1m);

        var target = Assert.Single(targets);
        Assert.Equal(product, target.ProductId);
        Assert.Equal(100m, target.Quantity);
    }

    // ── C9: explicit per-line skip ────────────────────────────────────────────────

    [Fact]
    public void C9_Skipped_Resolution_Drops_The_Line()
    {
        var product = Guid.NewGuid();
        var unit = Guid.NewGuid();
        var ingredient = Guid.NewGuid();
        var line = Line(product, unit, ingredientId: ingredient);
        var skip = new IngredientResolution(IngredientId.From(ingredient), IsSkipped: true, Allocations: []);

        var targets = CookConsumePlanner.Plan(
            [line], [skip], adHocLines: [], Catalog((product, true)), scale: 1m);

        Assert.Empty(targets);
    }

    // ── C11: variant split / swap ─────────────────────────────────────────────────

    [Fact]
    public void C11_Variant_Split_Emits_One_Target_Per_Allocation()
    {
        var product = Guid.NewGuid();
        var unit = Guid.NewGuid();
        var ingredient = Guid.NewGuid();
        var variantA = Guid.NewGuid();
        var variantB = Guid.NewGuid();
        var line = Line(product, unit, quantity: 500m, ingredientId: ingredient);
        var resolution = new IngredientResolution(
            IngredientId.From(ingredient),
            IsSkipped: false,
            Allocations:
            [
                new VariantAllocation(variantA, 300m, unit),
                new VariantAllocation(variantB, 200m, unit),
            ]);

        var targets = CookConsumePlanner.Plan(
            [line], [resolution], adHocLines: [], Catalog((variantA, true), (variantB, true)), scale: 2m);

        Assert.Equal(2, targets.Count);
        // Allocation quantities are used verbatim — scale is NOT re-applied to an explicit allocation.
        Assert.Contains(targets, t => t.ProductId == variantA && t.Quantity == 300m);
        Assert.Contains(targets, t => t.ProductId == variantB && t.Quantity == 200m);
        // The original product is never consumed when the ingredient is split.
        Assert.DoesNotContain(targets, t => t.ProductId == product);
    }

    [Fact]
    public void C11_Variant_Swap_Keeps_The_Line_Ingredient_Identity()
    {
        var product = Guid.NewGuid();
        var swap = Guid.NewGuid();
        var unit = Guid.NewGuid();
        var ingredient = Guid.NewGuid();
        var line = Line(product, unit, ingredientId: ingredient);
        var resolution = new IngredientResolution(
            IngredientId.From(ingredient), IsSkipped: false,
            Allocations: [new VariantAllocation(swap, 100m, unit)]);

        var targets = CookConsumePlanner.Plan(
            [line], [resolution], adHocLines: [], Catalog((swap, true)), scale: 1m);

        var target = Assert.Single(targets);
        Assert.Equal(swap, target.ProductId);
        Assert.Equal(IngredientId.From(ingredient), target.IngredientId);
    }

    // ── C12: untracked / unknown ─────────────────────────────────────────────────

    [Fact]
    public void C12_Untracked_Staple_Null_Quantity_Is_Skipped()
    {
        var product = Guid.NewGuid();
        var line = Line(product, Guid.NewGuid(), quantity: null);

        var targets = CookConsumePlanner.Plan(
            [line], resolutions: [], adHocLines: [], Catalog((product, true)), scale: 1m);

        Assert.Empty(targets);
    }

    [Fact]
    public void C12_Product_With_TrackStock_False_Is_Skipped()
    {
        var product = Guid.NewGuid();
        var line = Line(product, Guid.NewGuid());

        var targets = CookConsumePlanner.Plan(
            [line], resolutions: [], adHocLines: [], Catalog((product, false)), scale: 1m);

        Assert.Empty(targets);
    }

    [Fact]
    public void C12_Product_Absent_From_Catalog_Is_Skipped()
    {
        var line = Line(Guid.NewGuid(), Guid.NewGuid());

        var targets = CookConsumePlanner.Plan(
            [line], resolutions: [], adHocLines: [], catalogSummaries: Catalog(), scale: 1m);

        Assert.Empty(targets);
    }

    [Fact]
    public void C12_Untracked_Variant_Allocation_Is_Skipped_But_Tracked_Sibling_Kept()
    {
        var unit = Guid.NewGuid();
        var ingredient = Guid.NewGuid();
        var trackedVariant = Guid.NewGuid();
        var untrackedVariant = Guid.NewGuid();
        var line = Line(Guid.NewGuid(), unit, ingredientId: ingredient);
        var resolution = new IngredientResolution(
            IngredientId.From(ingredient), IsSkipped: false,
            Allocations:
            [
                new VariantAllocation(trackedVariant, 100m, unit),
                new VariantAllocation(untrackedVariant, 100m, unit),
            ]);

        var targets = CookConsumePlanner.Plan(
            [line], [resolution], adHocLines: [], Catalog((trackedVariant, true), (untrackedVariant, false)), scale: 1m);

        var target = Assert.Single(targets);
        Assert.Equal(trackedVariant, target.ProductId);
    }

    // ── D7: whole-inclusion skip ─────────────────────────────────────────────────

    [Fact]
    public void D7_WholeInclusion_Skip_Drops_Lines_Under_The_Prefix()
    {
        var direct = Guid.NewGuid();
        var underA = Guid.NewGuid();
        var deeper = Guid.NewGuid();
        var unit = Guid.NewGuid();
        var inclusionA = InclusionId.From(Guid.NewGuid());
        var inclusionB = InclusionId.From(Guid.NewGuid());

        var directLine = Line(direct, unit, path: []);
        var lineUnderA = Line(underA, unit, path: [inclusionA]);
        var deeperUnderA = Line(deeper, unit, path: [inclusionA, inclusionB]);

        var skip = IngredientResolution.WholeInclusionSkip([inclusionA]);

        var targets = CookConsumePlanner.Plan(
            [directLine, lineUnderA, deeperUnderA], [skip], adHocLines: [],
            Catalog((direct, true), (underA, true), (deeper, true)), scale: 1m);

        // Only the direct line survives; both lines beneath inclusion A (and its descendant) are dropped.
        var target = Assert.Single(targets);
        Assert.Equal(direct, target.ProductId);
    }

    [Fact]
    public void D7_WholeInclusion_Skip_Matches_Segment_Wise_Not_By_Longer_Or_Divergent_Path()
    {
        var unit = Guid.NewGuid();
        var inclusionA = InclusionId.From(Guid.NewGuid());
        var inclusionB = InclusionId.From(Guid.NewGuid());
        var inclusionC = InclusionId.From(Guid.NewGuid());
        var keptShallow = Guid.NewGuid();
        var keptDivergent = Guid.NewGuid();

        // Skip prefix is [A, B]. A line at [A] is NOT beneath it (prefix longer than the line); a line at
        // [A, C] diverges at the second segment so is NOT beneath it either.
        var shallow = Line(keptShallow, unit, path: [inclusionA]);
        var divergent = Line(keptDivergent, unit, path: [inclusionA, inclusionC]);
        var skip = IngredientResolution.WholeInclusionSkip([inclusionA, inclusionB]);

        var targets = CookConsumePlanner.Plan(
            [shallow, divergent], [skip], adHocLines: [],
            Catalog((keptShallow, true), (keptDivergent, true)), scale: 1m);

        Assert.Equal(2, targets.Count);
    }

    // ── D8: provenance ────────────────────────────────────────────────────────────

    [Fact]
    public void D8_Direct_Line_Has_Null_Provenance()
    {
        var product = Guid.NewGuid();
        var line = Line(product, Guid.NewGuid(), path: []);

        var targets = CookConsumePlanner.Plan(
            [line], resolutions: [], adHocLines: [], Catalog((product, true)), scale: 1m);

        var target = Assert.Single(targets);
        Assert.Null(target.SourceRecipeId);
    }

    [Fact]
    public void D8_Inclusion_Line_Carries_Owning_SubRecipe_Provenance()
    {
        var product = Guid.NewGuid();
        var sub = Guid.NewGuid();
        var inclusion = InclusionId.From(Guid.NewGuid());
        var line = Line(product, Guid.NewGuid(), path: [inclusion], sourceRecipeId: sub);

        var targets = CookConsumePlanner.Plan(
            [line], resolutions: [], adHocLines: [], Catalog((product, true)), scale: 1m);

        var target = Assert.Single(targets);
        Assert.Equal(sub, target.SourceRecipeId);
    }

    // ── Ad-hoc materialization (plantry-7zjm) ─────────────────────────────────────

    [Fact]
    public void AdHoc_Product_Materializes_With_Empty_Ingredient_And_Null_Provenance()
    {
        var product = Guid.NewGuid();
        var unit = Guid.NewGuid();
        var adHoc = new AdHocLine(product, 50m, unit);

        var targets = CookConsumePlanner.Plan(
            expandedLines: [], resolutions: [], [adHoc], Catalog((product, true)), scale: 3m);

        var target = Assert.Single(targets);
        Assert.Equal(product, target.ProductId);
        Assert.Equal(50m, target.Quantity); // ad-hoc quantity is verbatim — scale does not apply
        Assert.Equal(IngredientId.From(Guid.Empty), target.IngredientId);
        Assert.Null(target.SourceRecipeId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void AdHoc_NonPositive_Quantity_Is_Skipped(int quantity)
    {
        var product = Guid.NewGuid();
        var adHoc = new AdHocLine(product, quantity, Guid.NewGuid());

        var targets = CookConsumePlanner.Plan(
            expandedLines: [], resolutions: [], [adHoc], Catalog((product, true)), scale: 1m);

        Assert.Empty(targets);
    }

    [Fact]
    public void AdHoc_Missing_Unit_Is_Skipped()
    {
        var product = Guid.NewGuid();
        var adHoc = new AdHocLine(product, 10m, Guid.Empty);

        var targets = CookConsumePlanner.Plan(
            expandedLines: [], resolutions: [], [adHoc], Catalog((product, true)), scale: 1m);

        Assert.Empty(targets);
    }

    [Fact]
    public void AdHoc_Untracked_Product_Is_Skipped()
    {
        var product = Guid.NewGuid();
        var adHoc = new AdHocLine(product, 10m, Guid.NewGuid());

        var targets = CookConsumePlanner.Plan(
            expandedLines: [], resolutions: [], [adHoc], Catalog((product, false)), scale: 1m);

        Assert.Empty(targets);
    }

    [Fact]
    public void Ordering_Recipe_Targets_Precede_AdHoc_Targets()
    {
        var recipeProduct = Guid.NewGuid();
        var adHocProduct = Guid.NewGuid();
        var unit = Guid.NewGuid();
        var line = Line(recipeProduct, unit);
        var adHoc = new AdHocLine(adHocProduct, 10m, unit);

        var targets = CookConsumePlanner.Plan(
            [line], resolutions: [], [adHoc], Catalog((recipeProduct, true), (adHocProduct, true)), scale: 1m);

        Assert.Equal(2, targets.Count);
        Assert.Equal(recipeProduct, targets[0].ProductId);
        Assert.Equal(adHocProduct, targets[1].ProductId);
    }

    // ── CollectCandidateProductIds ────────────────────────────────────────────────

    [Fact]
    public void CandidateIds_Include_Own_Variant_And_AdHoc_Products()
    {
        var unit = Guid.NewGuid();
        var ingredient = Guid.NewGuid();
        var own = Guid.NewGuid();
        var variant = Guid.NewGuid();
        var adHocProduct = Guid.NewGuid();
        var line = Line(own, unit, ingredientId: ingredient);
        var resolution = new IngredientResolution(
            IngredientId.From(ingredient), IsSkipped: false,
            Allocations: [new VariantAllocation(variant, 100m, unit)]);
        var adHoc = new AdHocLine(adHocProduct, 10m, unit);

        var ids = CookConsumePlanner.CollectCandidateProductIds([line], [resolution], [adHoc]);

        Assert.Contains(own, ids);
        Assert.Contains(variant, ids);
        Assert.Contains(adHocProduct, ids);
    }

    [Fact]
    public void CandidateIds_Exclude_Untracked_Staple_And_Skipped_Inclusion_Lines()
    {
        var unit = Guid.NewGuid();
        var inclusion = InclusionId.From(Guid.NewGuid());
        var staple = Guid.NewGuid();
        var underSkip = Guid.NewGuid();
        var kept = Guid.NewGuid();

        var stapleLine = Line(staple, unit, quantity: null);
        var skippedLine = Line(underSkip, unit, path: [inclusion]);
        var keptLine = Line(kept, unit);
        var skip = IngredientResolution.WholeInclusionSkip([inclusion]);

        var ids = CookConsumePlanner.CollectCandidateProductIds(
            [stapleLine, skippedLine, keptLine], [skip], adHocLines: []);

        Assert.Contains(kept, ids);
        Assert.DoesNotContain(staple, ids);
        Assert.DoesNotContain(underSkip, ids);
    }

    [Fact]
    public void CandidateIds_Include_AdHoc_Product_Even_When_Quantity_Invalid()
    {
        // The candidate set is unconditional for ad-hoc products — the qty/unit guard (C12) is applied
        // only when planning, not when deciding which catalog facts to resolve.
        var adHocProduct = Guid.NewGuid();
        var adHoc = new AdHocLine(adHocProduct, 0m, Guid.Empty);

        var ids = CookConsumePlanner.CollectCandidateProductIds(
            expandedLines: [], resolutions: [], [adHoc]);

        Assert.Contains(adHocProduct, ids);
    }
}
