namespace Plantry.Intake.Domain;

public enum ImportStatus { Parsing, Ready, Committed, Discarded, Failed }

public static class ImportStatusExtensions
{
    public static string ToDbValue(this ImportStatus status) => status switch
    {
        ImportStatus.Parsing => "Parsing",
        ImportStatus.Ready => "Ready",
        ImportStatus.Committed => "Committed",
        ImportStatus.Discarded => "Discarded",
        ImportStatus.Failed => "Failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static ImportStatus Parse(string value) => value switch
    {
        "Parsing" => ImportStatus.Parsing,
        "Ready" => ImportStatus.Ready,
        "Committed" => ImportStatus.Committed,
        "Discarded" => ImportStatus.Discarded,
        "Failed" => ImportStatus.Failed,
        _ => throw new ArgumentException($"Unknown import status '{value}'.", nameof(value)),
    };
}
