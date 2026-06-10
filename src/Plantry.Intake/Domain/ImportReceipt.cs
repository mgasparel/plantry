using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Intake.Domain;

/// <summary>
/// 1:1 entity keyed by the same <see cref="ImportSessionId"/> as its owning session. Stored in a
/// separate table to keep the hot <c>import_session</c> row thin (binary receipt content is only
/// loaded when re-parsing).
/// </summary>
public sealed class ImportReceipt : Entity<ImportSessionId>
{
    public HouseholdId HouseholdId { get; private set; }
    public byte[] Content { get; private set; } = [];
    public string ContentType { get; private set; } = string.Empty;
    public long ByteSize { get; private set; }
    public string Sha256 { get; private set; } = string.Empty;
    public string? RawText { get; private set; }

    private ImportReceipt() { } // EF

    public static ImportReceipt Create(
        ImportSessionId sessionId,
        HouseholdId householdId,
        byte[] content,
        string contentType,
        string sha256,
        string? rawText = null) =>
        new()
        {
            Id = sessionId,
            HouseholdId = householdId,
            Content = content,
            ContentType = contentType,
            ByteSize = content.LongLength,
            Sha256 = sha256,
            RawText = rawText,
        };
}
