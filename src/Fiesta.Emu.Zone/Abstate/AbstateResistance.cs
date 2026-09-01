using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Abstate;

/// <summary>`so_AbnormalState_Resist` — whether a debuff is shrugged off before it is ever applied.
///
/// <para>Called at object vtable +0x634, and the two sides do completely different things. `roe_CriticalStun`
/// is one caller: a critical rolls for its stun, then the defender rolls to resist it.</para>
///
/// <para>Both roll <c>well512_GetRandom(1000)</c> and resist on a STRICT less-than, so a resistance of 0
/// never resists and one of 1000 always does.</para></summary>
public static class AbstateResistance
{
    /// <summary>`so_AbnormalState_Resist@ShineMob` (0x00416660) — a flat per-mob table.
    ///
    /// <code>
    /// if (mobData[0x14] == 0xFFFF)          return not resisted;   // the mob has no resist record
    /// type = abState-&gt;index-&gt;[0xF8]                                // 1..12, else not resisted
    /// resist = word [mob + 0x252D + 2*(type-1)]                    // 12 u16 permille
    /// return well512(1000) &lt; resist
    /// </code>
    ///
    /// <para>Twelve slots, one per resist type, straight off the mob's own record — no stats, no
    /// container, nothing the mob is wearing. A mob's resistance is a property of the species.</para></summary>
    /// <param name="resistPermille">The mob's twelve values, or an empty list for a mob whose record is
    /// absent (`0xFFFF`), which resists nothing.</param>
    /// <param name="resistType">`AbnormalStateInfo+0xF8`. Outside 1..12 nothing is resisted — including
    /// <b>0</b>, which the switch's <c>test edx,edx</c> rejects before the range check.</param>
    public static bool MobResists(IReadOnlyList<int> resistPermille, int resistType, uint draw)
    {
        if (resistType is < 1 or > 12 || resistPermille.Count < resistType) return false;
        return draw < resistPermille[resistType - 1];
    }

    /// <summary>`so_AbnormalState_Resist@ShinePlayer` (0x00416730) — two cluster halves, summed.
    ///
    /// <code>
    /// if (abState == NULL) return RESISTED;              // no definition: refuse the state
    /// slot = abState-&gt;debuffresist                       // a byte offset into a Parameter::Cluster
    /// resist = *(int*)(player + slot + 0x1158)           // 0x1158 - 0xFC0 = 0x198 -> Item.Rate
    ///        + *(int*)(player + slot + 0x1950)           // 0x1950 - 0xFC0 = 0x990 -> AbnormalState.Rate
    /// return well512(1000) &lt; resist
    /// </code>
    ///
    /// <para><b>`AbStateStr.debuffresist` is not a value — it is an offset.</b> Declared <c>int *</c> in
    /// the PDB, it is used as the byte offset of a stat slot, so the abstate definition names WHICH
    /// resistance stat applies to it and the player's own two rate halves supply the number.</para>
    ///
    /// <para><b>The two halves are ADDED, not multiplied</b> — and that is the second independent sighting
    /// of this pattern. The rate-eraser zeroes exactly slots 42..48 (`CriticalTB`, `RegistNone`, the four
    /// `Resist*`, `ResistGTI`), which is precisely the set used additively: `roe_CriticalRate` subtracts
    /// `CriticalTB` and this sums a `Resist*`. A rate slot read additively needs a 0 identity, and these
    /// have one.</para>
    ///
    /// <para>Which sharpens the open question in `FUTURE_TESTS.md`: `CriDamRate` is read additively by
    /// `roe_CriticalRate` too, yet the eraser seeds it with <b>1000</b>. Every other additive rate slot is
    /// zeroed. That is now an anomaly with two corroborating cases against it, not a lone puzzle.</para>
    ///
    /// <para>A null definition returns RESISTED, so an unknown abstate is refused rather than applied
    /// blind.</para></summary>
    /// <param name="resistSlot">The stat slot `debuffresist` points at — one of the `Resist*` group.</param>
    public static int PlayerResistPermille(ParameterContainer player, Stat resistSlot)
        => player.Rate(StatModifier.Item)[resistSlot]
         + player.Rate(StatModifier.AbnormalState)[resistSlot];

    /// <inheritdoc cref="PlayerResistPermille"/>
    public static bool PlayerResists(ParameterContainer player, Stat resistSlot, uint draw)
        => draw < PlayerResistPermille(player, resistSlot);

    /// <summary>The stat slots a `debuffresist` offset can select. Their common property is the one that
    /// matters: every one is in the rate-eraser's zero run, so "no resistance" really is 0.</summary>
    public static readonly IReadOnlyList<Stat> ResistSlots =
    [
        Stat.RegistNone, Stat.ResistPoison, Stat.ResistDeaseas,
        Stat.ResistCurse, Stat.ResistMoveSpdDown, Stat.ResistGTI,
    ];
}
