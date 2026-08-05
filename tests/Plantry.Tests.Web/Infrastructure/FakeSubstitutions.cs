using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Test-host registration for the Substitution edge seams (plantry-aqpa.1/.5). The real
/// <c>ISubstitutionReader</c>/<c>ISubstitutionRepository</c> read/write Recipes' own database; L4
/// factories that render the Catalog Product Detail page (which now composes both directly, per
/// plantry-aqpa.5) with faked Pantry/Catalog ports have no database, so they must stub these too —
/// otherwise the page throws trying to reach a Postgres connection the test host never configured.
/// Defaults to no edges / no-op writes, preserving the pre-plantry-aqpa.5 empty-substitutions
/// behaviour those pages had. Overload accepting a pre-built <see cref="FakeSubstitutionRepository"/>
/// lets a test seed edges up front and inspect writes after a POST — both the reader and the
/// repository share the SAME backing store, matching the real EF-backed pair's shared table.
/// </summary>
public static class FakeSubstitutionsRegistration
{
    public static IServiceCollection AddFakeSubstitutions(this IServiceCollection services) =>
        services.AddFakeSubstitutions(new FakeSubstitutionRepository());

    public static IServiceCollection AddFakeSubstitutions(
        this IServiceCollection services, FakeSubstitutionRepository repository)
    {
        services.RemoveAll<ISubstitutionReader>();
        services.AddSingleton<ISubstitutionReader>(new FakeSubstitutionReader(repository));
        services.RemoveAll<ISubstitutionRepository>();
        services.AddSingleton<ISubstitutionRepository>(repository);
        return services;
    }
}

/// <summary>Reads through to a <see cref="FakeSubstitutionRepository"/>'s in-memory store.</summary>
public sealed class FakeSubstitutionReader(FakeSubstitutionRepository repository) : ISubstitutionReader
{
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<SubstitutionEdge>>> ListByTargetProductIdsAsync(
        IReadOnlyList<Guid> targetProductIds, CancellationToken ct = default)
    {
        var byTarget = repository.Items
            .Where(s => targetProductIds.Contains(s.TargetProductId))
            .GroupBy(s => s.TargetProductId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SubstitutionEdge>)g.Select(ToEdge).ToList());
        return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<SubstitutionEdge>>>(byTarget);
    }

    public Task<IReadOnlyList<SubstitutionEdge>> ListTouchingProductAsync(
        Guid productId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SubstitutionEdge>>(
            repository.Items
                .Where(s => s.TargetProductId == productId || s.SubstituteProductId == productId)
                .Select(ToEdge)
                .ToList());

    private static SubstitutionEdge ToEdge(Substitution s) => new(
        s.Id.Value, s.TargetProductId, s.TargetQuantity, s.TargetUnitId,
        s.SubstituteProductId, s.SubstituteQuantity, s.SubstituteUnitId);
}

/// <summary>In-memory <see cref="ISubstitutionRepository"/> — mutable so a test can seed edges and
/// inspect writes made through <c>CreateSubstitution</c>/<c>DeleteSubstitution</c>.</summary>
public sealed class FakeSubstitutionRepository : ISubstitutionRepository
{
    public List<Substitution> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    public Task AddAsync(Substitution substitution, CancellationToken ct = default)
    {
        Items.Add(substitution);
        return Task.CompletedTask;
    }

    public void Remove(Substitution substitution) => Items.Remove(substitution);

    public Task<Substitution?> GetByIdAsync(SubstitutionId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.Id == id));

    public Task<Substitution?> FindByPairAsync(
        Guid substituteProductId, Guid targetProductId, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(
            s => s.SubstituteProductId == substituteProductId && s.TargetProductId == targetProductId));

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}
