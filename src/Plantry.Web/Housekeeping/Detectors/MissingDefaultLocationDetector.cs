using System.Security.Cryptography;
using System.Text;
using Plantry.Composition.Infrastructure;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Housekeeping;

/// <summary>D8: flags tracked concrete products that have no catalog default location.</summary>
public sealed class MissingDefaultLocationDetector(
    IStockFactsReadModel factsReadModel,
    ITenantContext tenant) : IProblemDetector
{
    private const string FingerprintLiteral = "d8-product-missing-default-location-v1";

    public DetectorId Id => DetectorId.ProductMissingDefaultLocation;
    public Severity Severity => Severity.Advisory;
    public string GroupTitle => "Products without a default location";
    public string GroupConsequence =>
        "No product-specific home is set, so new stock entries have no location prefilled and the product can appear in Take Stock's “No location” flow.";
    public string IconName => "i-location";

    public async Task<IReadOnlyList<Finding>> DetectAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return [];

        var bag = await factsReadModel.LoadAsync(ct);
        var fingerprint = Fingerprint();

        return bag.Products.Values
            .Where(product => product.TrackStock && !product.IsParent && product.DefaultLocationId is null)
            .OrderBy(product => product.Name, StringComparer.OrdinalIgnoreCase)
            .Select(product => new Finding(
                Id,
                product.ProductId,
                product.Name,
                "Default location not set",
                "New stock entries have no product-specific location prefilled; existing lots may still be stored in a physical location.",
                $"/Catalog/Products/{product.ProductId}",
                "Fix in Catalog",
                fingerprint))
            .ToList();
    }

    private static string Fingerprint() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FingerprintLiteral)));
}
