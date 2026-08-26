using Fiesta.Emu.Zone.Random;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`cWell512Random`.
///
/// <para><b>Unlike the other test classes in this repo, these expectations came from the ORACLE</b> — they
/// are values the real function in `Zone.exe` produced under emulation, not numbers derived from reading
/// it. `tools/verify_well512.py` compares 250-400 raw doubles per seed, bit-for-bit, and reports 1,050/1,050
/// exact across four seeds.</para>
///
/// <para>Exactness is the whole point here. The RNG feeds mob skill selection, so a sequence that is merely
/// plausible makes every chance-driven branch diverge from the server, and a simulation that cannot
/// reproduce the server's choices is not much of a simulation.</para></summary>
public class Well512Tests
{
    private static uint[] Sequential() => Enumerable.Range(1, 16).Select(i => (uint)i).ToArray();

    /// <summary>Captured from the real function with state = 1..16, index = 0.</summary>
    [Fact]
    public void MatchesTheServerSequenceFromAKnownState()
    {
        var rng = new cWell512Random(Sequential());

        rng.well512_GetRandom().ShouldBe(0.62689211824908853);
        rng.well512_GetRandom().ShouldBe(0.56976397195830941);
        rng.well512_GetRandom().ShouldBe(0.1751731182448566);
        rng.well512_GetRandom().ShouldBe(0.98657442582771182);
        rng.well512_GetRandom().ShouldBe(0.87725855270400643);
    }

    /// <summary>An all-zero state is a genuine fixed point: WELL512 cannot escape it, so the generator
    /// returns 0.0 forever. The server does exactly the same — this is the algorithm's behaviour, not a
    /// porting bug, and it is worth pinning so nobody "fixes" it later.</summary>
    [Fact]
    public void AnAllZeroStateIsAFixedPointAndYieldsZeroForever()
    {
        var rng = new cWell512Random(new uint[16]);
        for (var i = 0; i < 8; i++)
            rng.well512_GetRandom().ShouldBe(0.0);
    }

    [Fact]
    public void DrawsStayInTheUnitInterval()
    {
        var rng = new cWell512Random(Sequential());
        for (var i = 0; i < 500; i++)
        {
            var d = rng.well512_GetRandom();
            d.ShouldBeGreaterThanOrEqualTo(0.0);
            d.ShouldBeLessThan(1.0);
        }
    }

    [Fact]
    public void TheStartingIndexChangesTheSequence()
    {
        var a = new cWell512Random(Sequential(), index: 0).well512_GetRandom();
        var b = new cWell512Random(Sequential(), index: 7).well512_GetRandom();
        a.ShouldNotBe(b);
    }

    [Fact]
    public void TheIndexWrapsRatherThanRunningOffTheState()
    {
        // index is masked to 4 bits, so 16 and 0 are the same starting point.
        new cWell512Random(Sequential(), index: 16).well512_GetRandom()
            .ShouldBe(new cWell512Random(Sequential(), index: 0).well512_GetRandom());
    }

    // ---- the bounded overload -----------------------------------------------------------------------

    [Fact]
    public void TheBoundedDrawStaysBelowItsBound()
    {
        var rng = new cWell512Random(Sequential());
        for (var i = 0; i < 500; i++)
            rng.well512_GetRandom(100).ShouldBeLessThan(100u);
    }

    [Fact]
    public void ABoundOfOneAlwaysGivesZero()
    {
        var rng = new cWell512Random(Sequential());
        for (var i = 0; i < 20; i++)
            rng.well512_GetRandom(1).ShouldBe(0u);
    }

    /// <summary>The original divides by the bound with no guard, so zero faults. Returning some invented
    /// safe value would be a behaviour the server does not have.</summary>
    [Fact]
    public void ABoundOfZeroThrows_BecauseTheOriginalDividesByZero()
        => Should.Throw<DivideByZeroException>(() => new cWell512Random(Sequential()).well512_GetRandom(0));

    [Fact]
    public void ConstructionRejectsAStateThatIsNot16Words()
        => Should.Throw<ArgumentException>(() => new cWell512Random(new uint[8]));

    /// <summary>Same state in, same sequence out — the property the simulator actually depends on.</summary>
    [Fact]
    public void TheGeneratorIsReproducibleFromItsState()
    {
        var first = new cWell512Random(Sequential());
        var second = new cWell512Random(Sequential());
        for (var i = 0; i < 50; i++)
            second.well512_GetRandom().ShouldBe(first.well512_GetRandom());
    }
}
