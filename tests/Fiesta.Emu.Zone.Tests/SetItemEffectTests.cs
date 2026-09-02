using Fiesta.Emu.Zone.Skill;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`setitemskilleffect` — the matched-set bonus buffer, and what its slots mean.</summary>
public class SetItemEffectTests
{
    /// <summary>⭐ 17 enum values against 17 buffer slots is the check that settles the indexing. An
    /// earlier pass guessed `SkillEffectIncreaseType` (15 values, and it calls slot 2 "keeptime") and
    /// filed the mismatch rather than forcing it; `SetIndex` fits exactly and names slot 2
    /// `SET_DAMEGERATE`, which IS its observed use.</summary>
    [Fact]
    public void TheEnumFitsTheBufferExactly()
    {
        SetItemSkillEffect.SlotCount.ShouldBe(17);
        ((int)SetIndex.SET_DAMEGERATE).ShouldBe(2, "the slot smo_SkillBlast multiplies damage by");
        ((int)SetIndex.SET_PROBABILITYRATE).ShouldBe(13, "the slot that feeds the hit-rate path");
    }

    /// <summary>All 17 read 1000 with nothing staged — measured live from a zone at rest.</summary>
    [Fact]
    public void AnUnstagedBufferIsNeutralEverywhere()
    {
        var e = new SetItemSkillEffect();
        foreach (SetIndex i in Enum.GetValues<SetIndex>())
            if ((int)i < SetItemSkillEffect.SlotCount)
                e[i].ShouldBe(1000);
    }

    /// <summary>⭐ Set bonuses ADD their excess over 1000 — they do NOT compound. `siel_AppendEffect` does
    /// <c>slot += argument</c> then <c>slot += -1000</c> per piece, onto a buffer starting at 1000.
    ///
    /// <para>Two pieces of 1100 give 1200, not 1210. This is the exact opposite of
    /// <see cref="Fiesta.Emu.Zone.Combat.ItemActionResults"/>, which compounds with a truncating divide
    /// each step — the two mechanisms look interchangeable and are not.</para></summary>
    [Fact]
    public void PiecesAddTheirExcessRatherThanCompounding()
    {
        var e = SetItemSkillEffect.For(
        [
            (SetIndex.SET_DAMEGERATE, 1100),
            (SetIndex.SET_DAMEGERATE, 1100),
        ]);

        e[SetIndex.SET_DAMEGERATE].ShouldBe(1200);
        e[SetIndex.SET_DAMEGERATE].ShouldNotBe(1210, "compounding would give 1210");
    }

    /// <summary>A piece BELOW 1000 subtracts, because the contribution is a signed excess. Nothing floors
    /// it, so a malus is expressible.</summary>
    [Fact]
    public void APieceBelowNeutralSubtracts()
    {
        var e = SetItemSkillEffect.For([(SetIndex.SET_COOLTIMERATE, 800)]);
        e[SetIndex.SET_COOLTIMERATE].ShouldBe(800);

        var two = SetItemSkillEffect.For([(SetIndex.SET_COOLTIMERATE, 800), (SetIndex.SET_COOLTIMERATE, 900)]);
        two[SetIndex.SET_COOLTIMERATE].ShouldBe(700, "-200 and -100 from the 1000 base");
    }

    /// <summary>Slots are independent — one piece's contribution does not leak into another index.</summary>
    [Fact]
    public void SlotsAreIndependent()
    {
        var e = SetItemSkillEffect.For([(SetIndex.SET_DAMEGERATE, 1500)]);
        e[SetIndex.SET_DAMEGERATE].ShouldBe(1500);
        e[SetIndex.SET_PROBABILITYRATE].ShouldBe(1000);
        e[SetIndex.SET_HEALRATE].ShouldBe(1000);
    }
}
