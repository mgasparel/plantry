namespace Plantry.Deals.Application;

/// <summary>
/// Read port onto Catalog for product validation (deals-domain-model §7/§8). Before <c>ConfirmDeal</c>
/// commits a resolved product — into <see cref="DealMatchMemory"/> and the price observation — it checks
/// the product actually exists (and is live) in this household's catalog; a dangling <c>product_id</c>
/// would poison both the auto-confirm memory and the price history. Deals holds only the <c>Guid</c>
/// soft-ref (ADR-010/DM-3); the Web adapter implements this over Catalog's <c>IProductRepository</c>.
/// </summary>
public interface ICatalogProductReader
{
    /// <summary>True when a live (non-archived) product with this id exists in the current household's catalog.</summary>
    Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default);

    /// <summary>
    /// The current household's live, stock-eligible products, projected as <see cref="ProductCandidate"/>s
    /// for the stage-2 <see cref="IDealMatcher"/> (DJ2 step 4). Passed <b>in</b> to the matcher so the
    /// untrusted AI adapter never touches Catalog and can only ever suggest one of these ids (ADR-007) —
    /// the deal twin of Intake's <c>ICatalogHintProvider</c>. RLS-scoped to the armed household.
    /// </summary>
    Task<IReadOnlyList<ProductCandidate>> ListCandidatesAsync(CancellationToken ct = default);
}
