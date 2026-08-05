using Microsoft.Extensions.Logging;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Recipes.Application;

/// <summary>
/// Application service that authors a household-scoped <see cref="Substitution"/> edge (plantry-aqpa.1).
/// A duplicate directed pair — an edge already exists for this (substitute, target) pair — replaces the
/// existing edge's ratio rather than being rejected: the user is re-asserting the ratio now, mirroring
/// <c>Product.AddConversion</c>'s "a user-confirmed factor supersedes" rule. There is no separate
/// "UpdateSubstitution" command — this upsert shape covers both the create and the edit path (no edit in
/// v1: delete + recreate/re-author is the repair path, matching <c>ProductConversion</c>'s no-edit-UI
/// precedent).
/// </summary>
public sealed class CreateSubstitution(
    ISubstitutionRepository substitutions,
    ITenantContext tenant,
    IClock clock,
    ILogger<CreateSubstitution> logger)
{
    /// <summary>
    /// Authors the edge. The returned <see cref="Result{T}.Value"/> tells the caller whether this
    /// duplicate-pair upsert replaced an existing edge (<c>true</c>) or inserted a new one
    /// (<c>false</c>) — the command already knows the answer from its own <c>FindByPairAsync</c> lookup
    /// below, so a caller that needs to distinguish "added" from "replaced" (e.g. the product detail
    /// page's toast wording, plantry-aqpa.5) reads it here rather than re-deriving it with a second,
    /// redundant read through the write-side <see cref="ISubstitutionRepository"/> seam.
    /// </summary>
    public async Task<Result<bool>> ExecuteAsync(CreateSubstitutionCommand command, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
        {
            logger.LogWarning("Create substitution rejected — no authenticated household.");
            return Error.Unauthorized;
        }
        var household = HouseholdId.From(householdGuid);

        if (command.TargetProductId == Guid.Empty || command.SubstituteProductId == Guid.Empty)
        {
            logger.LogWarning("Create substitution rejected — missing target/substitute product id.");
            return Error.Custom(
                "Recipes.InvalidProduct", "A substitution must reference both a target and a substitute product.");
        }

        if (command.TargetUnitId == Guid.Empty || command.SubstituteUnitId == Guid.Empty)
        {
            logger.LogWarning("Create substitution rejected — missing target/substitute unit id.");
            return Error.Custom("Recipes.InvalidUnit", "A substitution must specify a unit on both sides.");
        }

        if (command.SubstituteProductId == command.TargetProductId)
        {
            logger.LogWarning(
                "Create substitution rejected — self-substitution for product {ProductId}.",
                command.TargetProductId);
            return Error.Custom("Recipes.SelfSubstitution", "A product cannot substitute for itself.");
        }

        if (command.TargetQuantity <= 0)
        {
            logger.LogWarning(
                "Create substitution rejected — non-positive target quantity {Quantity}.", command.TargetQuantity);
            return Error.Custom("Recipes.InvalidQuantity", "Target quantity must be strictly positive.");
        }

        if (command.SubstituteQuantity <= 0)
        {
            logger.LogWarning(
                "Create substitution rejected — non-positive substitute quantity {Quantity}.",
                command.SubstituteQuantity);
            return Error.Custom("Recipes.InvalidQuantity", "Substitute quantity must be strictly positive.");
        }

        var existing = await substitutions.FindByPairAsync(
            command.SubstituteProductId, command.TargetProductId, ct);

        bool replaced;
        if (existing is null)
        {
            var substitution = Substitution.Create(
                household,
                command.TargetProductId, command.TargetQuantity, command.TargetUnitId,
                command.SubstituteProductId, command.SubstituteQuantity, command.SubstituteUnitId,
                clock);
            await substitutions.AddAsync(substitution, ct);
            replaced = false;
        }
        else
        {
            existing.ReplaceRatio(
                command.TargetQuantity, command.TargetUnitId,
                command.SubstituteQuantity, command.SubstituteUnitId,
                clock);
            replaced = true;
        }

        await substitutions.SaveChangesAsync(ct);
        logger.LogInformation(
            "Substitution edge authored: {SubstituteProductId} -> {TargetProductId}.",
            command.SubstituteProductId, command.TargetProductId);
        return Result<bool>.Success(replaced);
    }
}

/// <summary>Input for <see cref="CreateSubstitution"/> — the directed (substitute, target) pair and its ratio.</summary>
public sealed record CreateSubstitutionCommand(
    Guid TargetProductId,
    decimal TargetQuantity,
    Guid TargetUnitId,
    Guid SubstituteProductId,
    decimal SubstituteQuantity,
    Guid SubstituteUnitId);
