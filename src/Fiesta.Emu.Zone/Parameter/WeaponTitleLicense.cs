namespace Fiesta.Emu.Zone.Parameter;

/// <summary>`WEAPON_TITLE_DATA` (64 bytes) — one level of a weapon LICENSE, keyed to a mob type.
///
/// <para>A license is earned by killing a particular monster and levels up with the kill count. It grants
/// two quite different things, and the split is the whole point of the mechanic:</para>
/// <list type="bullet">
///   <item><b>a damage bonus that applies ONLY against that mob type</b> (`MinAdd` / `MaxAdd`), and</item>
///   <item><b>a critical bonus that applies against everything</b> (one of the `SP` options), which also
///         grows as the license levels.</item>
/// </list></summary>
/// <param name="MobId">`MobID` — the monster this license is for.</param>
/// <param name="Level">`Level`.</param>
/// <param name="MobKillCount">`MobKillCount` — the kills this level requires.</param>
/// <param name="MinAdd">`MinAdd` — a permille RATE on the low weapon-damage bound.</param>
/// <param name="MaxAdd">`MaxAdd` — the same on the high bound.</param>
/// <param name="Options">`SP1`..`SP3` — up to three (Reference, Type, Value) triples.</param>
public sealed record WeaponTitleData(
    int MobId,
    int Level,
    uint MobKillCount,
    int MinAdd,
    int MaxAdd,
    IReadOnlyList<(int Reference, int Type, uint Value)>? Options = null);

/// <summary>`ShinePlayer::smo_ply_WeaponTitleSet` (0x00569CD0) — staging a weapon license into the
/// parameter container.
///
/// <para>⭐ <b>It is called from `smo_SwingDamage` with the DEFENDER's mob id</b>, so a license is re-staged
/// on every swing against whatever is being hit. That is how a bonus "only against Orcs" is implemented
/// without the container ever needing to know about targets: the container is rebuilt per swing.</para>
///
/// <para>⚠️ <b>The values land in the ordinary `WeaponTitle` cluster, not in special fields.</b> The
/// container is embedded in the object at +0xFC0 and `WeaponTitle.Rate` at container +0x660, so the
/// writes at object +0x1634/+0x1638/+0x1648/+0x164C/+0x16A0 are exactly:</para>
/// <code>
/// +0x1634 -> WeaponTitle.Rate[WCmin]       = MinAdd
/// +0x1638 -> WeaponTitle.Rate[WCmax]       = MaxAdd
/// +0x1648 -> WeaponTitle.Rate[MAmin]       = MinAdd     // the magic pair gets the same numbers
/// +0x164C -> WeaponTitle.Rate[MAmax]       = MaxAdd
/// +0x16A0 -> WeaponTitle.Rate[CriDamRate] += Value      // sp_WeaponTitleOption, ref 1 / type 1
/// </code>
///
/// <para>Which means <b>the damage half was already modelled</b> — the weapon accessors read
/// `WeaponTitle.Rate`, and `roe_CriticalRate` reads `WeaponTitle.Rate[CriDamRate]`. What was missing is
/// only the STAGING: nothing was populating the cluster. An object-relative offset that looks like a
/// bespoke field is worth resolving against the container before concluding it is one.</para>
///
/// <para>⭐ It also answers a long-standing question: <b>what zeroes `CriDamRate` after the eraser fill.</b>
/// The rate eraser seeds 1000 into almost every slot, yet a live container reads 0 at `CriDamRate` in
/// exactly the three clusters `roe_CriticalRate` sums. This function is one of them — it refills the
/// cluster from the erasers and then explicitly zeroes `CriDamRate` and `MagCriDamRate`, so a character
/// with no license has no weapon-title crit rather than a free 100%.</para></summary>
public static class WeaponTitleLicense
{
    /// <summary>`sp_WeaponTitleOption`'s only implemented case. Reference 1, Type 1 accumulates into
    /// <see cref="Stat.CriDamRate"/>; every other combination hits an "Invalid" log and does nothing.
    ///
    /// <para>So the crit bonus is the ONE option the server actually honours, which is consistent with it
    /// being the unconditional half of a license.</para></summary>
    public const int CritOptionReference = 1;

    /// <summary>The `Type` that pairs with <see cref="CritOptionReference"/>.</summary>
    public const int CritOptionType = 1;

    /// <summary>Whether an option triple does anything at all.</summary>
    public static bool OptionIsHonoured(int reference, int type)
        => reference == CritOptionReference && type == CritOptionType;

    /// <summary>Stage a license for one target, exactly as `smo_ply_WeaponTitleSet` does.
    ///
    /// <para>Call this at the START of every swing with the defender's mob id — not once at equip. Passing
    /// <c>null</c> for <paramref name="data"/> models "no license for this mob type", which is the common
    /// case and must still perform the reset: the previous target's bonus has to go.</para></summary>
    public static void Stage(ParameterContainer container, WeaponTitleData? data)
    {
        // The base implementation first: refill both halves from the erasers, then zero the two critical
        // rates. Order matters -- the rate eraser puts 1000 in CriDamRate and this takes it back out.
        //
        // `ParameterCluster.RateZeroedAfterErase` already listed WeaponTitle's two zeroed slots, inferred
        // from a live container read. THIS function is what does the zeroing, so the inference is now
        // sourced rather than merely observed.
        var plus = container.Plus(StatModifier.WeaponTitle);
        var rate = container.Rate(StatModifier.WeaponTitle);
        var freshPlus = ParameterCluster.Plus();
        var freshRate = ParameterCluster.RateFor(StatModifier.WeaponTitle);
        foreach (Stat stat in Enum.GetValues<Stat>())
        {
            plus[stat] = freshPlus[stat];
            rate[stat] = freshRate[stat];
        }

        if (data is null) return;

        // The conditional half. Both stat pairs get the same two numbers, so a caster's licence works on
        // magic attack the way a melee one works on weapon damage.
        rate[Stat.WCmin] = data.MinAdd;
        rate[Stat.WCmax] = data.MaxAdd;
        rate[Stat.MAmin] = data.MinAdd;
        rate[Stat.MAmax] = data.MaxAdd;

        // The unconditional half. ACCUMULATES (`add`), so three options of the honoured kind stack.
        foreach (var (reference, type, value) in data.Options ?? [])
            if (OptionIsHonoured(reference, type))
                rate[Stat.CriDamRate] += (int)value;
    }
}
