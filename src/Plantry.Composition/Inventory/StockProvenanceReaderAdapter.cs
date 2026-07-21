using Plantry.Intake.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Inventory;

/// <summary>
/// Composition-root adapter for <see cref="IStockProvenanceReader"/> (receipt-intake-history.md H4) — the
/// pantry-history "what produced this row" cross-link. Joins Intake (receipt sessions/lines) and Recipes
/// (cook events/recipes) to resolve a batch of journal rows to a display chip, exactly the seam
/// <see cref="Plantry.Web.Shopping.ShoppingRecipeReaderAdapter"/> plays for Shopping: Inventory itself never
/// takes a dependency on either context (Gate 2), the composition root does the joining.
///
/// <para>Intake rows resolve via <see cref="IImportSessionRepository.FindLinesForProvenanceAsync"/> — new-
/// style rows (post-H1, <c>SourceRef</c> = the committing line's own id) and legacy rows (pre-H1,
/// <c>SourceRef</c> null, reverse-resolved off the line's own <c>JournalId</c> column) in one batched call.
/// Cook rows resolve via <see cref="ICookEventRepository.GetRecipeIdsByCookEventIdsAsync"/> then
/// <see cref="IRecipeRepository.GetRecipeNamesByIdAsync"/>. Every lookup is tenant-scoped (the Intake/Recipes
/// repositories apply their own RLS query filters); a row that cannot be resolved is simply absent from the
/// result — the chip is progressive enhancement, never a dead link.</para>
/// </summary>
public sealed class StockProvenanceReaderAdapter(
    IImportSessionRepository sessions,
    ICookEventRepository cookEvents,
    IRecipeRepository recipes,
    ITenantContext tenant) : IStockProvenanceReader
{
    public async Task<IReadOnlyDictionary<Guid, ProvenanceChip>> ResolveAsync(
        IReadOnlyList<(Guid JournalId, StockSourceType SourceType, Guid? SourceRef)> rows,
        CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, ProvenanceChip>();
        if (rows.Count == 0 || tenant.HouseholdId is not { } hid)
            return result;

        var householdId = HouseholdId.From(hid);
        await ResolveIntakeAsync(rows, householdId, result, ct);
        await ResolveCookAsync(rows, result, ct);
        return result;
    }

    private async Task ResolveIntakeAsync(
        IReadOnlyList<(Guid JournalId, StockSourceType SourceType, Guid? SourceRef)> rows,
        HouseholdId householdId, Dictionary<Guid, ProvenanceChip> result, CancellationToken ct)
    {
        var intakeRows = rows.Where(r => r.SourceType == StockSourceType.Intake).ToList();
        if (intakeRows.Count == 0)
            return;

        // New-style rows (post-H1): SourceRef IS the committing line's own id. Legacy rows (pre-H1):
        // SourceRef is null, so the journal row's OWN id is reverse-matched against the line's JournalId.
        var newStyleLineIds = intakeRows.Where(r => r.SourceRef is not null).Select(r => r.SourceRef!.Value).ToList();
        var legacyJournalIds = intakeRows.Where(r => r.SourceRef is null).Select(r => r.JournalId).ToList();

        var lines = await sessions.FindLinesForProvenanceAsync(householdId, newStyleLineIds, legacyJournalIds, ct);
        if (lines.Count == 0)
            return;

        var byLineId = lines.ToDictionary(l => l.ImportLineId);
        var byJournalId = lines.ToDictionary(l => l.JournalId);

        foreach (var row in intakeRows)
        {
            var line = row.SourceRef is { } lineRef
                ? byLineId.GetValueOrDefault(lineRef)
                : byJournalId.GetValueOrDefault(row.JournalId);
            if (line is null)
                continue; // unresolvable — the line no longer exists, or matched no committed row

            var store = string.IsNullOrWhiteSpace(line.MerchantText) ? "Unknown store" : line.MerchantText;
            var date = line.PurchaseDate ?? DateOnly.FromDateTime(line.SessionCreatedAt.LocalDateTime);
            var label = $"{store} · {date:d MMM}";
            var href = $"/Intake/Session/{line.SessionId}#line-{line.ImportLineId}";
            result[row.JournalId] = new ProvenanceChip(ProvenanceChipKind.Intake, label, href);
        }
    }

    private async Task ResolveCookAsync(
        IReadOnlyList<(Guid JournalId, StockSourceType SourceType, Guid? SourceRef)> rows,
        Dictionary<Guid, ProvenanceChip> result, CancellationToken ct)
    {
        var cookRows = rows.Where(r => r.SourceType == StockSourceType.Cook && r.SourceRef is not null).ToList();
        if (cookRows.Count == 0)
            return;

        var cookEventIds = cookRows.Select(r => r.SourceRef!.Value).Distinct().ToList();
        var recipeIdsByCookEvent = await cookEvents.GetRecipeIdsByCookEventIdsAsync(cookEventIds, ct);
        if (recipeIdsByCookEvent.Count == 0)
            return;

        var recipeIds = recipeIdsByCookEvent.Values.Distinct().ToList();
        var namesByRecipeId = await recipes.GetRecipeNamesByIdAsync(recipeIds, ct);

        foreach (var row in cookRows)
        {
            if (!recipeIdsByCookEvent.TryGetValue(row.SourceRef!.Value, out var recipeId))
                continue; // cook event deleted/foreign-household — no chip

            if (!namesByRecipeId.TryGetValue(recipeId, out var name))
                continue; // recipe since deleted — no chip

            result[row.JournalId] = new ProvenanceChip(ProvenanceChipKind.Cook, name, $"/Recipes/{recipeId.Value}");
        }
    }
}
