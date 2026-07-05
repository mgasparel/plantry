namespace Plantry.Recipes.Application;

/// <summary>
/// Signals that an <see cref="IInventoryConsumer.ConsumeAsync"/> call could not decrement the pantry
/// because no <c>ProductConversion</c> bridged the ingredient unit to the product's stock unit
/// (Inventory surfaced <c>Catalog.UnresolvableConversion</c>) — plantry-qll2.6.
/// <para>
/// This is the discriminated counterpart to the plain <see cref="InvalidOperationException"/> the
/// consumer throws for a genuine no-stock product. The <see cref="IInventoryConsumer"/> port keeps its
/// throw-based contract; the adapter inspects <c>Result.Error.Code</c> and throws THIS type only for the
/// unit-gap case, so callers (<c>CookRecipe</c>, <c>ReconcilePendingCooks</c>) can discriminate a
/// deferrable unit gap (retry when the math arrives) from a hard shortfall (never retry) by catching
/// this <b>before</b> <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// Deliberately does NOT derive from <see cref="InvalidOperationException"/>: the existing no-stock
/// catch must not swallow it, or a unit gap would be mis-recorded as <c>Shorted</c> and never
/// retro-applied.
/// </para>
/// </summary>
public sealed class DeferredUnitGapException : Exception
{
    public DeferredUnitGapException(string message) : base(message) { }
}
