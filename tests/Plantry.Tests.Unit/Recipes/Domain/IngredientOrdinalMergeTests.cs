using Plantry.Recipes.Domain;

namespace Plantry.Tests.Unit.Recipes.Domain;

/// <summary>
/// L1 domain tests for <see cref="IngredientOrdinalMerge"/> — the pure N3 shared-space ordinal merge
/// (recipe-composition.md) extracted from <c>AuthorRecipe.ExecuteAsync</c> (plantry-xgmb). These exercise
/// the canonicalisation directly, with no ports and no orchestrator: sparse/out-of-order ordinals collapse
/// to contiguous 0-based positions, and equal ordinals break ties ingredient-before-inclusion in
/// source-index order (matching the original stable concat-then-order inline merge).
/// </summary>
public sealed class IngredientOrdinalMergeTests
{
    private static (bool IsInclusion, int SourceIndex, int Position) Tuple(IngredientOrdinalMerge.MergedLine m) =>
        (m.IsInclusion, m.SourceIndex, m.Position);

    [Fact]
    public void Empty_Inputs_Produce_Empty_Merge()
    {
        var merged = IngredientOrdinalMerge.Merge([], []);
        Assert.Empty(merged);
    }

    [Fact]
    public void Ingredients_Only_Renumbers_Sparse_Ordinals_Contiguously_In_Ordinal_Order()
    {
        // Sparse, out-of-order ingredient ordinals: 9, 2, 5 (source indices 0, 1, 2).
        var merged = IngredientOrdinalMerge.Merge([9, 2, 5], []);

        // Ascending position order maps to ordinal order 2 (idx1) → 5 (idx2) → 9 (idx0).
        Assert.Equal(
            [(false, 1, 0), (false, 2, 1), (false, 0, 2)],
            merged.Select(Tuple));
    }

    [Fact]
    public void Inclusions_Only_Renumbers_Contiguously()
    {
        var merged = IngredientOrdinalMerge.Merge([], [7, 3]);

        // 3 (idx1) → 7 (idx0), positions 0,1; both flagged as inclusions.
        Assert.Equal(
            [(true, 1, 0), (true, 0, 1)],
            merged.Select(Tuple));
    }

    [Fact]
    public void Interleaved_Lines_Merge_By_Ordinal_Across_Both_Types()
    {
        // Ingredients at ordinals 0 and 4; inclusions at ordinals 2 and 6. Interleave: I0, N2, I4, N6.
        var merged = IngredientOrdinalMerge.Merge([0, 4], [2, 6]);

        Assert.Equal(
            [(false, 0, 0), (true, 0, 1), (false, 1, 2), (true, 1, 3)],
            merged.Select(Tuple));
    }

    [Fact]
    public void Equal_Ordinals_Keep_Ingredients_Before_Inclusions_Stable()
    {
        // Every line shares ordinal 0. Stable tie-break: all ingredients (index order) before all
        // inclusions (index order) — the original concat-then-stable-order behaviour.
        var merged = IngredientOrdinalMerge.Merge([0, 0], [0, 0]);

        Assert.Equal(
            [(false, 0, 0), (false, 1, 1), (true, 0, 2), (true, 1, 3)],
            merged.Select(Tuple));
    }

    [Fact]
    public void Assigned_Positions_Are_A_Contiguous_Zero_Based_Permutation()
    {
        var merged = IngredientOrdinalMerge.Merge([5, 1, 9], [3, 8]);

        // Five lines total → positions are exactly {0,1,2,3,4}.
        Assert.Equal([0, 1, 2, 3, 4], merged.Select(m => m.Position).OrderBy(p => p));
        // Each source line appears exactly once.
        Assert.Equal(3, merged.Count(m => !m.IsInclusion));
        Assert.Equal(2, merged.Count(m => m.IsInclusion));
    }

    [Fact]
    public void Ingredient_And_Inclusion_Sharing_An_Ordinal_Order_Ingredient_First()
    {
        // Ingredient (idx0) and inclusion (idx0) both at ordinal 1, with an ingredient at ordinal 0 before.
        var merged = IngredientOrdinalMerge.Merge([0, 1], [1]);

        Assert.Equal(
            [(false, 0, 0), (false, 1, 1), (true, 0, 2)],
            merged.Select(Tuple));
    }
}
