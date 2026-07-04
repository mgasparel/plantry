using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Intake.Application;

/// <summary>
/// Synchronous receipt-intake driver (SPEC §2a): starts an <see cref="ImportSession"/>, persists the
/// uploaded image as the 1:1 <see cref="ImportReceipt"/>, then runs the untrusted AI parse and lands the
/// proposal on the session. On success the session goes <c>Ready</c> with one <see cref="ImportLine"/> per
/// parsed line (raw AI payload quarantined in <c>raw_parse</c>, ACL provenance); on failure it goes
/// <c>Failed</c> with the parser's soft-fail message. Either way a row is persisted so the user has a
/// record of the attempt.
/// </summary>
public sealed class ParseSessionCommand(
    byte[] imageBytes,
    string contentType,
    Guid userId,
    IImportSessionRepository sessions,
    IReceiptParser parser,
    ICatalogHintProvider hints,
    IClock clock,
    ITenantContext tenant,
    ILogger<ParseSessionCommand>? logger = null)
{
    public async Task<Result<ImportSessionId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;
        if (imageBytes.Length == 0)
            return Error.Custom("Intake.EmptyImage", "The uploaded receipt image is empty.");

        var household = HouseholdId.From(householdId);

        var session = ImportSession.Start(household, ImportSourceType.Receipt, userId, clock);
        var sha = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();
        var receipt = ImportReceipt.Create(session.Id, household, imageBytes, contentType, sha);

        await sessions.AddAsync(session, ct);
        await sessions.AddReceiptAsync(receipt, ct);
        await sessions.SaveChangesAsync(ct);

        var catalogHints = await hints.GetHintsAsync(ct);
        var parse = await parser.ParseAsync(imageBytes, contentType, catalogHints, ct);

        if (parse.HasError)
        {
            logger?.LogWarning(
                "Receipt parse failed for session {SessionId}: {ParseError}.",
                session.Id.Value, parse.ErrorMessage);
            session.MarkParsingFailed(parse.ErrorMessage!);
            await sessions.SaveChangesAsync(ct);
            return session.Id;
        }

        foreach (var line in parse.Lines)
        {
            var alternatives = line.Alternatives?
                .Select(a => new AlternativeCandidate(a.ProductId, a.ProductName, a.Confidence))
                .ToList();

            // Weight→each ground truth (plantry-1mu): an estimate is only carried when the model produced
            // a positive each-count AND the line was weight-priced (a weight + weight-unit label present),
            // so the receipt weight is preserved distinctly from the generic Suggested* fields.
            var hasEachEstimate = line is { EstimatedEachCount: > 0m, Quantity: not null, UnitLabel: not null };
            var receiptWeight = hasEachEstimate ? line.Quantity : null;
            var receiptWeightUnitLabel = hasEachEstimate ? line.UnitLabel : null;
            var estimatedEachCount = hasEachEstimate ? line.EstimatedEachCount : null;
            var estimatedEachConfidence = hasEachEstimate ? MapConfidence(line.EstimatedEachConfidence) : (SuggestedConfidence?)null;

            session.AddLine(line.LineNo, line.ReceiptText, MapConfidence(line.Confidence), line.RawJson,
                line.SuggestedProductId, line.SuggestedProductName, line.Quantity, line.UnitLabel, line.Price,
                alternatives is { Count: >= ImportLine.MinAlternativesForSuggestion } ? alternatives : null,
                receiptWeight, receiptWeightUnitLabel, estimatedEachCount, estimatedEachConfidence);
        }

        session.MarkReady(parse.MerchantText, clock.UtcNow, parse.Metadata);
        await sessions.SaveChangesAsync(ct);
        logger?.LogInformation(
            "Receipt parsed successfully. Session {SessionId} is Ready with {LineCount} line(s).",
            session.Id.Value, parse.Lines.Count);
        return session.Id;
    }

    private static SuggestedConfidence MapConfidence(string? confidence) => confidence?.Trim().ToLowerInvariant() switch
    {
        "high" => SuggestedConfidence.High,
        "low" => SuggestedConfidence.Low,
        _ => SuggestedConfidence.None,
    };
}
