using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>⚠️ Tests that run thousands of simulation ticks, serialised against each other.
///
/// <para><c>AThousandTicksRunFastEnoughToIterateOn</c> asserts WALL-CLOCK time — a hundred simulated
/// seconds in under two — which is a real and useful guarantee, and a fragile one: xUnit runs test
/// classes in parallel, so adding the scenario matrix and the driver-fights runs made it share a CPU with
/// several 4,000-tick sweeps and fail intermittently. It passed on its own every time.</para>
///
/// <para>Loosening the threshold would have thrown away the guarantee to hide the contention. Putting
/// every heavy simulation class in one collection makes them run one at a time instead, so the timing
/// assertion measures the simulator rather than the scheduler.</para></summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeavySimulationCollection
{
    public const string Name = "heavy simulation";
}
