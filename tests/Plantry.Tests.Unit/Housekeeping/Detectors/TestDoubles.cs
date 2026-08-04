using Plantry.SharedKernel.Tenancy;
using Plantry.Composition.Infrastructure;
using Plantry.Web.Housekeeping;

namespace Plantry.Tests.Unit.Housekeeping.Detectors;

/// <summary>
/// Fast L1/L2 unit-test coverage for the 7 Tidy Up detectors' pure C# math, substituting in-memory fakes
/// for <see cref="IStockFactsReadModel"/>/<see cref="IRecipeFactsReadModel"/> (ADR-021/ADR-024 Phase A —
/// these interfaces were extracted specifically so tests can do this; see the doc comment atop
/// <c>StockFactsReadModel.cs</c>). Restores the fast-test coverage the 7 old fake-port-based detector
/// test files provided before ADR-021/ADR-024 Phase A retired those ports — this suite is additive to,
/// not a replacement for, the L3 Postgres-fixture tests in
/// <c>tests/Plantry.Tests.Integration/Housekeeping/{StockDetectorsTests,RecipeDetectorsTests}.cs</c>,
/// which remain the SQL/schema contract proof.
/// </summary>
internal sealed class FakeTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

internal sealed class FakeStockFactsReadModel(StockFactsBag bag) : IStockFactsReadModel
{
    public Task<StockFactsBag> LoadAsync(CancellationToken ct = default) => Task.FromResult(bag);
}

internal sealed class FakeRecipeFactsReadModel(RecipeFactsBag bag) : IRecipeFactsReadModel
{
    public Task<RecipeFactsBag> LoadAsync(CancellationToken ct = default) => Task.FromResult(bag);
}
