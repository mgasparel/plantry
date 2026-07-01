using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Xunit;

namespace Plantry.Tests.Unit.Deals.Domain;

/// <summary>
/// L1 unit tests for <see cref="DealMatchMemory"/> (§6): remember (positive), remember-negative,
/// repoint (correction), and forget.
/// </summary>
public sealed class DealMatchMemoryTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Store = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly NormalizedName Name = DealNormalizer.Normalize("2% Milk");

    [Fact(DisplayName = "Remember stores a positive mapping and stamps the normalizer version + raw name")]
    public void Remember_StoresPositiveMapping()
    {
        var memory = DealMatchMemory.Remember(Household, Store, Name, "2% Milk", Product, User, new TestClock());

        Assert.Equal(Store, memory.StoreId);
        Assert.Equal(Name.Value, memory.NormalizedName);
        Assert.Equal("2% Milk", memory.RawName);
        Assert.Equal(Name.NormalizerVersion, memory.NormalizerVersion);
        Assert.Equal(Product, memory.ProductId);
        Assert.Equal(User, memory.LastConfirmedByUserId);
    }

    [Fact(DisplayName = "RememberNegative stores a null product (negative memory)")]
    public void RememberNegative_StoresNullProduct()
    {
        var memory = DealMatchMemory.RememberNegative(Household, Store, Name, "2% Milk", User, new TestClock());

        Assert.Null(memory.ProductId);
        Assert.Equal(Name.Value, memory.NormalizedName);
    }

    [Fact(DisplayName = "Repoint rewrites the mapping to a different product (a correction)")]
    public void Repoint_RewritesMapping()
    {
        var clock = new TestClock();
        var memory = DealMatchMemory.Remember(Household, Store, Name, "2% Milk", Product, User, clock);
        var newProduct = Guid.NewGuid();
        var newUser = Guid.NewGuid();

        memory.Repoint(newProduct, newUser, clock.Advance(TimeSpan.FromMinutes(1)));

        Assert.Equal(newProduct, memory.ProductId);
        Assert.Equal(newUser, memory.LastConfirmedByUserId);
        Assert.Equal(clock.UtcNow, memory.UpdatedAt);
    }

    [Fact(DisplayName = "Forget turns a positive memory into a negative one")]
    public void Forget_ClearsProduct()
    {
        var memory = DealMatchMemory.Remember(Household, Store, Name, "2% Milk", Product, User, new TestClock());

        memory.Forget(new TestClock());

        Assert.Null(memory.ProductId);
    }
}
