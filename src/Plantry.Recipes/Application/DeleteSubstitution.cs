using Microsoft.Extensions.Logging;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;

namespace Plantry.Recipes.Application;

/// <summary>
/// Application service that deletes a <see cref="Substitution"/> edge (plantry-aqpa.1) — no edit in v1,
/// so this is the repair path alongside <see cref="CreateSubstitution"/>'s replace-on-duplicate upsert.
/// No-op (success) when the edge does not exist — deleting an already-gone edge is not an error, the same
/// idempotent-clear convention as <see cref="ClearRecipeRating"/>. The <c>GetByIdAsync</c> lookup is
/// already household-scoped by the RLS query filter, so a cross-household id resolves to "not found"
/// (silently a no-op) rather than needing a separate tenant check here.
/// </summary>
public sealed class DeleteSubstitution(
    ISubstitutionRepository substitutions,
    ILogger<DeleteSubstitution> logger)
{
    public async Task<Result> ExecuteAsync(DeleteSubstitutionCommand command, CancellationToken ct = default)
    {
        var existing = await substitutions.GetByIdAsync(command.SubstitutionId, ct);
        if (existing is null)
            return Result.Success();

        substitutions.Remove(existing);
        await substitutions.SaveChangesAsync(ct);
        logger.LogInformation("Substitution edge {SubstitutionId} deleted.", command.SubstitutionId.Value);
        return Result.Success();
    }
}

/// <summary>Input for <see cref="DeleteSubstitution"/>.</summary>
public sealed record DeleteSubstitutionCommand(SubstitutionId SubstitutionId);
