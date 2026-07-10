using Xunit;

namespace Plantry.Tests.Unit.SharedKernel;

/// <summary>
/// Collection definition for the domain-telemetry tests (<c>DomainTelemetryTests</c>, <c>CookRecipeTests</c>, which
/// join by the string name <c>"DomainMeterListenerTests"</c>). Those tests subscribe a <b>process-wide</b>
/// <see cref="System.Diagnostics.Metrics.MeterListener"/> to the <c>Plantry.Domain</c> meter and assert exact
/// counter deltas — so ANY other test exercising the same counters while the listener is active (e.g. Intake
/// <c>CommitSession</c> in <c>CommitSessionCommandTests</c> / <c>ReviewCommandsTests</c>) leaks a spurious
/// increment and makes the delta assertion flaky.
///
/// <para><see cref="Xunit.CollectionDefinitionAttribute.DisableParallelization"/> runs this collection in isolation
/// from EVERY other collection — not merely serialised within itself — closing the cross-collection meter-leak race
/// the test comments describe. Without this definition the <c>[Collection("DomainMeterListenerTests")]</c> members
/// serialise only against each other while still running concurrently with the rest of the assembly, so the leak
/// surfaces whenever scheduling happens to overlap a counter-emitting test.</para>
/// </summary>
[CollectionDefinition("DomainMeterListenerTests", DisableParallelization = true)]
public sealed class DomainMeterListenerCollection;
