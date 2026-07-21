using Plantry.Intake.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Unit.Intake.Application;
using Plantry.Web.Inventory;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="StockProvenanceReaderAdapter"/> — receipt-intake-history.md H4: the composition
/// join that resolves a batch of journal rows to a display chip. Covers the new-style Intake row (SourceRef
/// = the line's own id), the legacy fallback (SourceRef null, reverse-resolved off the line's JournalId),
/// the Cook row (recipe name via CookEvent → Recipe), and the degrade-chip-less cases (deleted recipe,
/// foreign-household session, unresolvable line).
/// </summary>
public sealed class StockProvenanceReaderAdapterTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();

    private StockProvenanceReaderAdapter Adapter(
        FakeImportSessionRepository sessions, TestCookEventRepository cookEvents, TestRecipeRepository recipes, Guid? household = null) =>
        new(sessions, cookEvents, recipes, new TestTenantContext(household ?? _householdId));

    // ── Intake — new-style (SourceRef = the committing line's own id) ──────────────────────────────

    [Fact]
    public async Task Resolves_a_new_style_intake_row_to_a_receipt_chip()
    {
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        var line = session.AddLine(1, "Milk", SuggestedConfidence.High, null);
        session.MarkReady("Costco", Clock.UtcNow, new ReceiptMetadata(PurchaseDate: new DateOnly(2026, 7, 18)));
        line.Confirm(Guid.CreateVersion7(), null, 1m, Guid.CreateVersion7(), Guid.CreateVersion7(), null, 5m);
        var journalId = Guid.NewGuid();
        line.MarkCommitted(journalId, null);
        session.MarkCommitted(Clock.UtcNow);

        var sessions = new FakeImportSessionRepository();
        sessions.Sessions.Add(session);

        var chips = await Adapter(sessions, new TestCookEventRepository(), new TestRecipeRepository())
            .ResolveAsync([(journalId, StockSourceType.Intake, line.Id.Value)]);

        var chip = Assert.Single(chips).Value;
        Assert.Equal(ProvenanceChipKind.Intake, chip.Kind);
        Assert.Contains("Costco", chip.Label);
        Assert.Contains("18 Jul", chip.Label);
        Assert.Equal(session.Id.Value, chip.TargetId);
        Assert.Equal(line.Id.Value, chip.LineAnchorId);
    }

    [Fact]
    public async Task Unknown_store_renders_as_unknown_store_in_the_chip_label()
    {
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        var line = session.AddLine(1, "Milk", SuggestedConfidence.High, null);
        session.MarkReady(null, Clock.UtcNow); // blank merchant
        line.Confirm(Guid.CreateVersion7(), null, 1m, Guid.CreateVersion7(), Guid.CreateVersion7(), null, 5m);
        var journalId = Guid.NewGuid();
        line.MarkCommitted(journalId, null);
        session.MarkCommitted(Clock.UtcNow);

        var sessions = new FakeImportSessionRepository();
        sessions.Sessions.Add(session);

        var chips = await Adapter(sessions, new TestCookEventRepository(), new TestRecipeRepository())
            .ResolveAsync([(journalId, StockSourceType.Intake, line.Id.Value)]);

        Assert.Contains("Unknown store", Assert.Single(chips).Value.Label);
    }

    // ── Intake — legacy fallback (SourceRef null, reverse-resolved off JournalId) ───────────────────

    [Fact]
    public async Task Resolves_a_legacy_intake_row_via_the_lines_own_JournalId()
    {
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        var line = session.AddLine(1, "Eggs", SuggestedConfidence.High, null);
        session.MarkReady("Loblaws", Clock.UtcNow);
        line.Confirm(Guid.CreateVersion7(), null, 1m, Guid.CreateVersion7(), Guid.CreateVersion7(), null, 4m);
        var journalId = Guid.NewGuid();
        line.MarkCommitted(journalId, null); // pre-H1 semantics: journal's own SourceRef would have been null
        session.MarkCommitted(Clock.UtcNow);

        var sessions = new FakeImportSessionRepository();
        sessions.Sessions.Add(session);

        // SourceRef is null (as a pre-H1 journal row would be) — resolution falls back to matching the
        // journal row's own id against the line's JournalId column.
        var chips = await Adapter(sessions, new TestCookEventRepository(), new TestRecipeRepository())
            .ResolveAsync([(journalId, StockSourceType.Intake, null)]);

        Assert.Contains("Loblaws", Assert.Single(chips).Value.Label);
    }

    // ── Cook ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolves_a_cook_row_to_a_recipe_chip()
    {
        var cookEventId = Guid.NewGuid();
        var recipeId = RecipeId.New();
        var cookEvents = new TestCookEventRepository();
        cookEvents.RecipeIdByCookEventId[cookEventId] = recipeId;
        var recipes = new TestRecipeRepository();
        recipes.NameByRecipeId[recipeId] = "Shakshuka";

        var journalId = Guid.NewGuid();
        var chips = await Adapter(new FakeImportSessionRepository(), cookEvents, recipes)
            .ResolveAsync([(journalId, StockSourceType.Cook, cookEventId)]);

        var chip = Assert.Single(chips).Value;
        Assert.Equal(ProvenanceChipKind.Cook, chip.Kind);
        Assert.Equal("Shakshuka", chip.Label);
        Assert.Equal(recipeId.Value, chip.TargetId);
        Assert.Null(chip.LineAnchorId);
    }

    [Fact]
    public async Task Deleted_recipe_degrades_chip_less()
    {
        var cookEventId = Guid.NewGuid();
        var recipeId = RecipeId.New();
        var cookEvents = new TestCookEventRepository();
        cookEvents.RecipeIdByCookEventId[cookEventId] = recipeId;
        var recipes = new TestRecipeRepository(); // recipe never registered — "deleted"

        var chips = await Adapter(new FakeImportSessionRepository(), cookEvents, recipes)
            .ResolveAsync([(Guid.NewGuid(), StockSourceType.Cook, cookEventId)]);

        Assert.Empty(chips);
    }

    [Fact]
    public async Task Unknown_cook_event_degrades_chip_less()
    {
        var chips = await Adapter(new FakeImportSessionRepository(), new TestCookEventRepository(), new TestRecipeRepository())
            .ResolveAsync([(Guid.NewGuid(), StockSourceType.Cook, Guid.NewGuid())]);

        Assert.Empty(chips);
    }

    // ── Degrade cases ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Foreign_household_session_degrades_chip_less()
    {
        var otherHousehold = Guid.NewGuid();
        var session = ImportSession.Start(HouseholdId.From(otherHousehold), ImportSourceType.Receipt, _userId, Clock);
        var line = session.AddLine(1, "Milk", SuggestedConfidence.High, null);
        session.MarkReady("Costco", Clock.UtcNow);
        line.Confirm(Guid.CreateVersion7(), null, 1m, Guid.CreateVersion7(), Guid.CreateVersion7(), null, 5m);
        var journalId = Guid.NewGuid();
        line.MarkCommitted(journalId, null);
        session.MarkCommitted(Clock.UtcNow);

        var sessions = new FakeImportSessionRepository();
        sessions.Sessions.Add(session);

        // Reading as _householdId (a different household than the session's owner).
        var chips = await Adapter(sessions, new TestCookEventRepository(), new TestRecipeRepository())
            .ResolveAsync([(journalId, StockSourceType.Intake, line.Id.Value)]);

        Assert.Empty(chips);
    }

    [Fact]
    public async Task Manual_rows_are_never_offered_to_the_reader_and_never_resolve()
    {
        var chips = await Adapter(new FakeImportSessionRepository(), new TestCookEventRepository(), new TestRecipeRepository())
            .ResolveAsync([(Guid.NewGuid(), StockSourceType.Manual, null)]);

        Assert.Empty(chips);
    }

    [Fact]
    public async Task Empty_input_returns_empty_without_any_household()
    {
        var chips = await Adapter(new FakeImportSessionRepository(), new TestCookEventRepository(), new TestRecipeRepository(), household: null)
            .ResolveAsync([]);

        Assert.Empty(chips);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────────────────────────

    private sealed class TestTenantContext(Guid? householdId) : ITenantContext
    {
        public Guid? HouseholdId { get; } = householdId;
    }

    private sealed class TestCookEventRepository : ICookEventRepository
    {
        public Dictionary<Guid, RecipeId> RecipeIdByCookEventId { get; } = [];

        public Task<IReadOnlyDictionary<Guid, RecipeId>> GetRecipeIdsByCookEventIdsAsync(
            IReadOnlyCollection<Guid> cookEventIds, CancellationToken ct = default)
        {
            IReadOnlyDictionary<Guid, RecipeId> result = cookEventIds
                .Where(RecipeIdByCookEventId.ContainsKey)
                .ToDictionary(id => id, id => RecipeIdByCookEventId[id]);
            return Task.FromResult(result);
        }

        public Task AddAsync(CookEvent cookEvent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CookEvent>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CookEvent>>([]);
        public Task<IReadOnlyList<CookEvent>> ListWithPendingLinesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CookEvent>>([]);
        public Task<IReadOnlyList<CookEvent>> ListWithDeferredUnitGapLinesForProductsAsync(
            IReadOnlyCollection<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CookEvent>>([]);
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestRecipeRepository : IRecipeRepository
    {
        public Dictionary<RecipeId, string> NameByRecipeId { get; } = [];

        public Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(
            IReadOnlyList<RecipeId> ids, CancellationToken ct = default)
        {
            IReadOnlyDictionary<RecipeId, string> result = ids
                .Where(NameByRecipeId.ContainsKey)
                .ToDictionary(id => id, id => NameByRecipeId[id]);
            return Task.FromResult(result);
        }

        public Task AddAsync(Recipe recipe, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default) => Task.FromResult<Recipe?>(null);
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Recipe>>([]);
        public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
        public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<RecipeInclusionEdge>> ListInclusionEdgesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RecipeInclusionEdge>>([]);
        public Task<IReadOnlySet<RecipeId>> GetIncluderIdsAsync(RecipeId subRecipeId, bool transitive = false, CancellationToken ct = default) => Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
    }
}
