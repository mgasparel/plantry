namespace Plantry.Deals.Domain;

/// <summary>
/// The deterministic, reproducible normalization of a raw advertised item name (DD4/DL-O6). With a
/// <c>StoreId</c> it is the <see cref="DealMatchMemory"/> lookup key. Produced only by
/// <see cref="DealNormalizer"/> (pure, never AI-derived); carries the <see cref="NormalizerVersion"/>
/// that produced it so a normalizer bump can trigger a one-time backfill rather than silent decay.
/// </summary>
public readonly record struct NormalizedName(string Value, int NormalizerVersion)
{
    public override string ToString() => Value;
}
