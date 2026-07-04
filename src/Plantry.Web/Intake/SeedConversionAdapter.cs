using Microsoft.Extensions.Logging;
using Plantry.Catalog.Domain;
using Plantry.Intake.Application;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Intake;

/// <summary>
/// Web-side adapter for <see cref="ISeedConversionPort"/> — records the AI-suggested weight→each
/// conversion learned at intake commit (plantry-1mu) on the Catalog <see cref="Product"/> aggregate,
/// via <see cref="Product.AddConversion"/> with <see cref="ConversionSource.AiSuggested"/> provenance
/// (plantry-3k44). Reads/writes Catalog directly so Plantry.Intake stays free of any Catalog dependency,
/// mirroring <see cref="CreateProductAdapter"/>. The product is loaded with its conversions so the
/// aggregate's idempotent suggestion merge rule applies (no duplicate/fighting factors).
///
/// <para>Never throws on an unknown/absent product or a non-positive factor: a learned-conversion seed is
/// best-effort convenience — its failure must not abort a line's stock/price commit. It logs a warning
/// and returns instead.</para>
/// </summary>
public sealed class SeedConversionAdapter(
    IProductRepository products,
    IClock clock,
    ILogger<SeedConversionAdapter> logger) : ISeedConversionPort
{
    public async Task SeedAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor, CancellationToken ct = default)
    {
        if (factor <= 0m || fromUnitId == toUnitId)
        {
            logger.LogWarning(
                "Skipping weight→each conversion seed for product {ProductId}: invalid factor {Factor} or identical units.",
                productId, factor);
            return;
        }

        var pid = ProductId.From(productId);
        var loaded = await products.ListWithConversionsAsync([pid], ct);
        var product = loaded.SingleOrDefault(p => p.Id == pid);
        if (product is null)
        {
            logger.LogWarning(
                "Skipping weight→each conversion seed: product {ProductId} not found.", productId);
            return;
        }

        product.AddConversion(UnitId.From(fromUnitId), UnitId.From(toUnitId), factor, clock, ConversionSource.AiSuggested);
        await products.SaveChangesAsync(ct);

        logger.LogInformation(
            "Seeded AI-suggested weight→each conversion for product {ProductId}: {FromUnit}→{ToUnit} factor {Factor}.",
            productId, fromUnitId, toUnitId, factor);
    }
}
