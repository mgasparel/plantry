namespace Plantry.Intake.Domain;

/// <summary>
/// Signals that another receipt-review request won the staged-product normalized-name insert race.
/// The infrastructure repository clears its failed EF change tracker before rethrowing this signal so
/// the application command can reload the session and reuse or reject the now-persisted alias.
/// </summary>
public sealed class StagedProductNameConflictException(Exception innerException) : Exception(
    "A staged product with this normalized name was created concurrently.", innerException);
