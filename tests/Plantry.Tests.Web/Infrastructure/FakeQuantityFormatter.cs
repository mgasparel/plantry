using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Catalog.Domain;
using Plantry.Recipes.Application;
using Plantry.Web.Recipes;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// L4 test double for <see cref="IQuantityFormatter"/>. The live adapter loads the household's units from
/// Catalog's <c>IUnitRepository</c> (a Postgres seam the WAF suite never boots), so — exactly as every Cook/
/// Details factory already swaps <see cref="IUnitConverter"/> for an in-memory fake — this formats through
/// the SAME <see cref="QuantityFormatting"/> logic the live adapter uses, over a fixed set of units the
/// fixture supplies. With no units it degrades to the historical <c>0.###</c> decimal in the authored unit,
/// which keeps every existing (Decimal-styled, integer-quantity) fixture's render byte-identical.
/// </summary>
public sealed class FakeQuantityFormatter(IReadOnlyList<Unit> units) : IQuantityFormatter
{
    public Task<IReadOnlyDictionary<string, FormattedQuantity>> FormatAsync(
        IReadOnlyList<QuantityFormatRequest> requests, CancellationToken ct = default) =>
        Task.FromResult(QuantityFormatting.Format(requests, units));
}

public static class FakeQuantityFormatterExtensions
{
    /// <summary>
    /// Replaces <see cref="IQuantityFormatter"/> with a <see cref="FakeQuantityFormatter"/> over the given
    /// <paramref name="units"/> (empty by default — decimal passthrough). Call in every WAF factory that
    /// renders the Cook or Details page, alongside the existing <c>IUnitConverter</c> fake registration.
    /// </summary>
    public static IServiceCollection AddFakeQuantityFormatter(
        this IServiceCollection services, IReadOnlyList<Unit>? units = null)
    {
        services.RemoveAll<IQuantityFormatter>();
        services.AddSingleton<IQuantityFormatter>(new FakeQuantityFormatter(units ?? []));
        return services;
    }
}
