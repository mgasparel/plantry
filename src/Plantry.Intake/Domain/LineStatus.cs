namespace Plantry.Intake.Domain;

public enum LineStatus { Pending, Confirmed, Dismissed, Committed }

public static class LineStatusExtensions
{
    public static string ToDbValue(this LineStatus status) => status switch
    {
        LineStatus.Pending => "Pending",
        LineStatus.Confirmed => "Confirmed",
        LineStatus.Dismissed => "Dismissed",
        LineStatus.Committed => "Committed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static LineStatus Parse(string value) => value switch
    {
        "Pending" => LineStatus.Pending,
        "Confirmed" => LineStatus.Confirmed,
        "Dismissed" => LineStatus.Dismissed,
        "Committed" => LineStatus.Committed,
        _ => throw new ArgumentException($"Unknown line status '{value}'.", nameof(value)),
    };
}
