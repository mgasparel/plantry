namespace Plantry.Intake.Domain;

public enum SuggestedConfidence { High, Low, None }

public static class SuggestedConfidenceExtensions
{
    public static string ToDbValue(this SuggestedConfidence confidence) => confidence switch
    {
        SuggestedConfidence.High => "High",
        SuggestedConfidence.Low => "Low",
        SuggestedConfidence.None => "None",
        _ => throw new ArgumentOutOfRangeException(nameof(confidence)),
    };

    public static SuggestedConfidence Parse(string value) => value switch
    {
        "High" => SuggestedConfidence.High,
        "Low" => SuggestedConfidence.Low,
        "None" => SuggestedConfidence.None,
        _ => throw new ArgumentException($"Unknown suggested confidence '{value}'.", nameof(value)),
    };
}
