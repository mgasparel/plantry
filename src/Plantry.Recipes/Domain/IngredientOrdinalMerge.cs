namespace Plantry.Recipes.Domain;

/// <summary>
/// Pure, stateless helper that canonicalises the shared ordinal space of a recipe's ingredient and
/// inclusion lines (recipe-composition.md N3). Given the author-supplied ordinals of each line type it
/// merges them into a single sequence — ordered by ordinal, ties keeping ingredients before inclusions
/// and each in source-index order — and assigns contiguous 0-based positions across the union, so N3
/// (shared-space contiguity) holds however the author interleaved the two line types.
///
/// <para>Extracted from <c>AuthorRecipe.ExecuteAsync</c> (plantry-xgmb) so the merge can be unit-tested
/// directly, without standing up the orchestrator and its ports. It issues <b>zero</b> IO and depends on
/// nothing but the ordinals — the caller maps the returned <see cref="MergedLine.SourceIndex"/> back to
/// its resolved line.</para>
/// </summary>
public static class IngredientOrdinalMerge
{
    /// <summary>
    /// One entry in the canonicalised union of ingredient + inclusion lines: which list it came from
    /// (<see cref="IsInclusion"/>), its index within that list (<see cref="SourceIndex"/>), and its
    /// assigned contiguous position in the merged sequence (<see cref="Position"/>).
    /// </summary>
    public readonly record struct MergedLine(bool IsInclusion, int SourceIndex, int Position);

    /// <summary>
    /// Merges the two ordinal lists into the canonical sequence and assigns positions. Stable ordering:
    /// LINQ <c>OrderBy</c> is stable, so equal ordinals retain input order — every ingredient (in index
    /// order) precedes every inclusion (in index order) sharing that ordinal, exactly as the original
    /// concat-then-order inline merge did. Returned in ascending position order.
    /// </summary>
    public static IReadOnlyList<MergedLine> Merge(
        IReadOnlyList<int> ingredientOrdinals, IReadOnlyList<int> inclusionOrdinals)
    {
        var union = ingredientOrdinals
            .Select((ordinal, index) => (Ordinal: ordinal, IsInclusion: false, Index: index))
            .Concat(inclusionOrdinals.Select((ordinal, index) => (Ordinal: ordinal, IsInclusion: true, Index: index)))
            .OrderBy(x => x.Ordinal)
            .ToList();

        var merged = new List<MergedLine>(union.Count);
        for (var position = 0; position < union.Count; position++)
        {
            var item = union[position];
            merged.Add(new MergedLine(item.IsInclusion, item.Index, position));
        }
        return merged;
    }
}
