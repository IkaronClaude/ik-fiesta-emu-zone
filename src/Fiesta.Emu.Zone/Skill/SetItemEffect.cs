namespace Fiesta.Emu.Zone.Skill;

/// <summary>`SetIndex` — what each slot of `setitemskilleffect` means.
///
/// <para><b>17 values against the buffer's 17 `unsigned long`s</b>, which is the check that settles it.
/// An earlier pass guessed at `SkillEffectIncreaseType` and filed the mismatch rather than force it: that
/// enum has 15 values and calls slot 2 "keeptime" where the code plainly uses slot 2 as a damage
/// multiplier. `SetIndex` calls slot 2 <see cref="SET_DAMEGERATE"/>, and the count matches exactly.</para>
///
/// <para>Found by typing the WRITER rather than by pattern-matching names: `siel_AppendEffect` accumulates
/// `[esi+0x20]` as the index and `[esi+0x24]` as the value, and those are
/// `SetItemData::EffectDescription.seteffect` (a `SetIndex`) and `.setargument`.</para>
///
/// <para>The five `smo_SkillBlast` reads now read as sense: <see cref="SET_DAMEGERATE"/> scales the
/// damage and <see cref="SET_PROBABILITYRATE"/> the hit chance, with heal, HP-drain and aggro alongside
/// them.</para></summary>
public enum SetIndex
{
    SET_NONE = 0,
    SET_HEALRATE = 1,
    /// <summary>Slot 2 — read at `smo_SkillBlast+0x93B` to scale the finished damage.</summary>
    SET_DAMEGERATE = 2,
    SET_ACRATE = 3,
    SET_ABSTATEKEEPTIMERATE = 4,
    SET_SPCONSUMRATE = 5,
    SET_HPSPRECOVERYRATE = 6,
    SET_COOLTIMERATE = 7,
    SET_DEXRATE = 8,
    SET_HPDRAINRATE = 9,
    SET_MOVESPEEDRATE = 10,
    SET_THRATE = 11,
    SET_AGGRORATE = 12,
    /// <summary>Slot 13 — read at `smo_SkillBlast+0x312`, feeding the hit-rate path.</summary>
    SET_PROBABILITYRATE = 13,
    SET_HPSPMAXRATE = 14,
    SET_ABSTATERATE = 15,
    SET_DAMEGEIGNORERATE = 16,
}

/// <summary>`setitemskilleffect` (0x1325EDB0) — the matched-equipment-SET bonuses staged for whoever is
/// currently being processed.
///
/// <para>It is a global and it is NOT server configuration: `smo_ply_SetItemEffect` rebuilds it per
/// character (`ShinePlayer` walks the set list at +0x2A7E0, count at +0x2A7F4), and `sbec_Routine`
/// snapshots all 17 dwords before firing queued effects precisely because the damage path is about to
/// overwrite them.</para>
///
/// <para>⭐ <b>The accumulation is in EXCESS of 1000, not a product.</b> `siel_AppendEffect` does two adds
/// per contributing piece:</para>
/// <code>
/// setitemskilleffect[desc.seteffect] += desc.setargument;
/// setitemskilleffect[desc.seteffect] += -1000;
/// </code>
/// <para>so each piece contributes <c>argument - 1000</c> onto a buffer that starts at 1000. Two pieces of
/// 1100 give 1200, not 1210 — set bonuses ADD their excess rather than compounding. That is the opposite
/// of the item-action results in <see cref="Fiesta.Emu.Zone.Combat.ItemActionResults"/>, which compound;
/// the two look interchangeable and are not.</para></summary>
public sealed class SetItemSkillEffect
{
    /// <summary>The buffer's length, and `MAX_SETINDEX`.</summary>
    public const int SlotCount = 17;

    /// <summary>What a slot holds with no set bonus staged — confirmed by reading all 17 out of a live
    /// zone at rest.</summary>
    public const int Neutral = 1000;

    private readonly int[] _slots = [.. Enumerable.Repeat(Neutral, SlotCount)];

    public int this[SetIndex index] => _slots[(int)index];

    /// <summary>`siel_AppendEffect` — add one set piece's contribution.</summary>
    public void Append(SetIndex index, int argument)
        => _slots[(int)index] += argument - Neutral;

    /// <summary>Rebuild from a character's contributing pieces, as `smo_ply_SetItemEffect` does.</summary>
    public static SetItemSkillEffect For(IEnumerable<(SetIndex Index, int Argument)> pieces)
    {
        var e = new SetItemSkillEffect();
        foreach (var (index, argument) in pieces)
            e.Append(index, argument);
        return e;
    }
}
