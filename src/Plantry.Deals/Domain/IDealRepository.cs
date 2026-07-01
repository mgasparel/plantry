namespace Plantry.Deals.Domain;

/// <summary>
/// Read/write port for the <see cref="Deal"/> aggregate (§5 / DJ4). RLS-scoped to the current household
/// by <c>DealsDbContext</c>, so every query returns only the signed-in household's rows. The confirm /
/// reject orchestration (P5-5) saves after <b>each</b> aggregate mutation so its cross-context commit is
/// resumable — see <c>ConfirmDeal</c>.
/// </summary>
public interface IDealRepository
{
    Task<Deal?> FindAsync(DealId id, CancellationToken ct = default);

    Task AddAsync(Deal deal, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
