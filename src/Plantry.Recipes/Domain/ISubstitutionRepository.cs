namespace Plantry.Recipes.Domain;

/// <summary>
/// Write-side repository for the <see cref="Substitution"/> aggregate (plantry-aqpa.1). Read-heavy batch
/// queries for other consumers live on <see cref="Plantry.Recipes.Application.ISubstitutionReader"/>
/// instead — this interface only carries the CRUD shape <c>CreateSubstitution</c>/<c>DeleteSubstitution</c>
/// need.
/// </summary>
public interface ISubstitutionRepository
{
    Task AddAsync(Substitution substitution, CancellationToken ct = default);

    void Remove(Substitution substitution);

    Task<Substitution?> GetByIdAsync(SubstitutionId id, CancellationToken ct = default);

    /// <summary>
    /// Finds the single edge for a (substitute, target) directed pair in the current household — the
    /// UNIQUE (household_id, substitute_product_id, target_product_id) lookup <c>CreateSubstitution</c>
    /// keys its replace-on-duplicate upsert on.
    /// </summary>
    Task<Substitution?> FindByPairAsync(
        Guid substituteProductId, Guid targetProductId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
