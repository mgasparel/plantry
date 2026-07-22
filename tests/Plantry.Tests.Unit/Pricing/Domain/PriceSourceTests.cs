using Plantry.Pricing.Domain;

namespace Plantry.Tests.Unit.Pricing.Domain;

/// <summary>
/// Pins the <see cref="PriceSource"/> string-mapping round trip (pricing.md source taxonomy) —
/// the only guard against an unknown <c>source</c> value, since the DB column carries no CHECK
/// constraint (that guard lives purely in <see cref="PriceSourceExtensions"/>).
/// </summary>
public sealed class PriceSourceTests
{
    [Theory]
    [InlineData(PriceSource.Purchase, "Purchase")]
    [InlineData(PriceSource.Deal, "Deal")]
    [InlineData(PriceSource.Manual, "Manual")]
    public void ToDbValue_Maps_Each_Source_To_Its_String(PriceSource source, string expected) =>
        Assert.Equal(expected, source.ToDbValue());

    [Theory]
    [InlineData("Purchase", PriceSource.Purchase)]
    [InlineData("Deal", PriceSource.Deal)]
    [InlineData("Manual", PriceSource.Manual)]
    public void Parse_Maps_Each_String_To_Its_Source(string value, PriceSource expected) =>
        Assert.Equal(expected, PriceSourceExtensions.Parse(value));

    [Fact]
    public void Parse_Throws_On_Unknown_Value() =>
        Assert.Throws<ArgumentException>(() => PriceSourceExtensions.Parse("Rebate"));
}
