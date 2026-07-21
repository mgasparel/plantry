using Plantry.Housekeeping.Application;
using Plantry.Housekeeping.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Housekeeping.Application;

internal sealed class FakeTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

/// <summary>An <see cref="IProblemDetector"/> test double that returns a fixed, caller-supplied finding list.</summary>
internal sealed class FakeDetector(
    DetectorId id, Severity severity, IReadOnlyList<Finding> findings,
    string groupTitle = "Group", string groupConsequence = "Consequence", string iconName = "i-scale")
    : IProblemDetector
{
    public DetectorId Id => id;
    public Severity Severity => severity;
    public string GroupTitle => groupTitle;
    public string GroupConsequence => groupConsequence;
    public string IconName => iconName;

    public Task<IReadOnlyList<Finding>> DetectAsync(CancellationToken ct = default) => Task.FromResult(findings);
}

internal sealed class FakeDismissalRepository : IDismissalRepository
{
    private readonly List<Dismissal> _dismissals = [];

    public IReadOnlyList<Dismissal> All => _dismissals;

    public void Seed(Dismissal dismissal) => _dismissals.Add(dismissal);

    public Task<IReadOnlyList<Dismissal>> ListForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Dismissal>>(_dismissals.Where(d => d.HouseholdId == householdId).ToList());

    public Task<Dismissal?> FindAsync(HouseholdId householdId, DetectorId detectorId, Guid subjectId, CancellationToken ct = default) =>
        Task.FromResult(_dismissals.SingleOrDefault(d =>
            d.HouseholdId == householdId && d.DetectorId == detectorId && d.SubjectId == subjectId));

    public Task AddAsync(Dismissal dismissal, CancellationToken ct = default)
    {
        _dismissals.Add(dismissal);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Dismissal dismissal, CancellationToken ct = default)
    {
        _dismissals.Remove(dismissal);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeTidyUpBadgeCache : ITidyUpBadgeCache
{
    private readonly Dictionary<Guid, int> _counts = [];
    public int InvalidateCallCount { get; private set; }

    public int? LastSetCount => _counts.Count == 0 ? null : _counts.Values.Last();

    public Task<int?> TryGetAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(_counts.TryGetValue(householdId.Value, out var count) ? count : (int?)null);

    public Task SetAsync(HouseholdId householdId, int count, CancellationToken ct = default)
    {
        _counts[householdId.Value] = count;
        return Task.CompletedTask;
    }

    public Task InvalidateAsync(HouseholdId householdId, CancellationToken ct = default)
    {
        InvalidateCallCount++;
        _counts.Remove(householdId.Value);
        return Task.CompletedTask;
    }
}
