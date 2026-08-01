using Plantry.Intake.Domain;

namespace Plantry.Tests.Unit.Intake.Domain;

/// <summary>
/// Covers the <see cref="ImportSourceType"/> DB-value mapping — the closed set EF's value
/// converter and the DB CHECK constraint both rely on (plantry-45ba.1 added Manual).
/// </summary>
public sealed class ImportSourceTypeTests
{
    [Theory]
    [InlineData(ImportSourceType.Receipt, "Receipt")]
    [InlineData(ImportSourceType.Manual, "Manual")]
    public void ToDbValue_Roundtrips_Through_Parse(ImportSourceType value, string dbValue)
    {
        Assert.Equal(dbValue, value.ToDbValue());
        Assert.Equal(value, ImportSourceTypeExtensions.Parse(dbValue));
    }

    [Fact]
    public void Parse_Rejects_Unknown_Value()
    {
        Assert.Throws<ArgumentException>(() => ImportSourceTypeExtensions.Parse("Email"));
    }
}
