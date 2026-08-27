namespace Fiesta.Emu.Zone.Parameter;

/// <summary>`Parameter::Container` — every layer of a character's stats, and the rule that combines them.
///
/// <para><b>The layout is read, not guessed.</b> `Container::c_clear` (0x0043C370) seeds fifteen clusters at
/// a stride of 0xCC (51 dwords), each from one of two eraser templates, and WHICH eraser it uses is what
/// identifies the cluster:</para>
///
/// <code>
/// +0x000  plus     base            +0x4C8  rate     Upgrade.Rate
/// +0x0CC  plus     Item.Plus       +0x594  plus     WeaponTitle.Plus
/// +0x198  rate     Item.Rate       +0x660  rate     WeaponTitle.Rate
/// +0x264  plus     ItemPowerRate.Plus   +0x72C  plus  PassiveSkill.Plus
/// +0x330  rate     ItemPowerRate.Rate   +0x7F8  rate  PassiveSkill.Rate
/// +0x3FC  plus     Upgrade.Plus         +0x8C4  plus  AbnormalState.Plus
///                                       +0x990  rate  AbnormalState.Rate
///                                       +0xA5C  plus  LastTune.Plus
///                                       +0xB28  rate  LastTune.Rate
///                                       +0xBF4           the total
/// </code>
///
/// <para>One base cluster followed by seven (Plus, Rate) pairs, in <see cref="StatModifier"/> order. The
/// cross-check is that `c_MakeTotal` only ever uses <c>+=</c> on a plus-seeded cluster and <c>*=</c> on a
/// rate-seeded one — ten operations, no exceptions. A wrong mapping would have mismatched.</para></summary>
public sealed class ParameterContainer
{
    /// <summary>`Container::c_Storepure` fills this — the character's own stats before any modifier.</summary>
    public ParameterCluster Base { get; } = ParameterCluster.Plus();

    private readonly ParameterCluster[] _plus;
    private readonly ParameterCluster[] _rate;

    public ParameterContainer()
    {
        var sources = Enum.GetValues<StatModifier>().Length;
        _plus = new ParameterCluster[sources];
        _rate = new ParameterCluster[sources];
        for (var i = 0; i < sources; i++)
        {
            _plus[i] = ParameterCluster.Plus();
            _rate[i] = ParameterCluster.Rate();
        }
    }

    /// <summary>The flat half of one source.</summary>
    public ParameterCluster Plus(StatModifier source) => _plus[(int)source];

    /// <summary>The permille half of one source.</summary>
    public ParameterCluster Rate(StatModifier source) => _rate[(int)source];

    public ParameterCluster Half(StatModifier source, StatHalf half)
        => half == StatHalf.Rate ? Rate(source) : Plus(source);

    /// <summary>How many primaries `c_MakeTotal` floors — Str, Con, Dex, Int, Men.</summary>
    private const int PrimaryCount = 5;

    /// <summary>`Container::c_MakeTotal` (0x004C9A40) — combine every layer into the effective stats.
    ///
    /// <para><b>The order is the formula.</b> Addition and permille scaling do not commute under truncating
    /// division, so moving any step changes results. The original runs exactly this sequence, starting from
    /// a zeroed total:</para>
    ///
    /// <code>
    /// total  = 0
    /// total += base                    total += Upgrade.Plus
    /// total += Item.Plus               total += PassiveSkill.Plus
    /// total *= ItemPowerRate.Rate      total += AbnormalState.Plus
    /// total *= AbnormalState.Rate      total += LastTune.Plus
    /// total *= PassiveSkill.Rate       total *= LastTune.Rate
    /// </code>
    ///
    /// <para>Note what is NOT here: Item.Rate, ItemPowerRate.Plus, Upgrade.Rate and both WeaponTitle halves
    /// never reach the total. They are not dead — the damage formula reads them directly. A stat system
    /// that folded everything into one total would lose them.</para>
    ///
    /// <para>The three rate steps land BETWEEN the two groups of additions, so gear's flat bonus is scaled
    /// by buffs while an upgrade's is not. That asymmetry is the reason this is a ported sequence and not a
    /// tidy sum.</para></summary>
    /// <summary>The six `ChangeByConditionParam` blocks, keyed on how much HP the owner is MISSING.
    ///
    /// <para>These sit in the container's SECOND TIER, past `Total` — the region this project had decoded
    /// but connected to nothing. The PDB names them, and `roe_AttackPower` / `roe_DefendPower` are the
    /// eleven call sites that read them:</para>
    ///
    /// <list type="table">
    ///   <item><term>+0x0CE0 / +0x0CFC</term><description>`PassiveHPDownRateWCMin` / `Max` — read by every
    ///         PHYSICAL `roe_AttackPower`</description></item>
    ///   <item><term>+0x0D18 / +0x0D34</term><description>`PassiveHPDownRateMAMin` / `Max` — the magical
    ///         pair, read the same way</description></item>
    ///   <item><term>+0x0D50 / +0x0D6C</term><description>`PassiveHPDownRateAC` / `MR` — read by
    ///         `roe_DefendPower`, NOT by `roe_AC` / `roe_MR` themselves</description></item>
    /// </list>
    ///
    /// <para>All default to <see cref="ChangeByConditionParam.None"/>, which contributes zero at every
    /// condition — so a character without the passive behaves exactly as before this existed.</para></summary>
    public ChangeByConditionParam PassiveHpDownWcMin { get; set; } = ChangeByConditionParam.None;
    public ChangeByConditionParam PassiveHpDownWcMax { get; set; } = ChangeByConditionParam.None;
    public ChangeByConditionParam PassiveHpDownMaMin { get; set; } = ChangeByConditionParam.None;
    public ChangeByConditionParam PassiveHpDownMaMax { get; set; } = ChangeByConditionParam.None;
    public ChangeByConditionParam PassiveHpDownAc { get; set; } = ChangeByConditionParam.None;
    public ChangeByConditionParam PassiveHpDownMr { get; set; } = ChangeByConditionParam.None;

    public ParameterCluster MakeTotal()
    {
        var total = ParameterCluster.Plus();

        total.Add(Base);
        total.Add(Plus(StatModifier.Item));
        total.ApplyRate(Rate(StatModifier.ItemPowerRate));
        total.ApplyRate(Rate(StatModifier.AbnormalState));
        total.ApplyRate(Rate(StatModifier.PassiveSkill));
        total.Add(Plus(StatModifier.Upgrade));
        total.Add(Plus(StatModifier.PassiveSkill));
        total.Add(Plus(StatModifier.AbnormalState));
        total.Add(Plus(StatModifier.LastTune));
        total.ApplyRate(Rate(StatModifier.LastTune));

        // The tail of c_MakeTotal: the five primaries are floored at 1, so no stack of debuffs can zero
        // them. Nothing else is clamped.
        total.FloorSlots(firstSlot: 0, count: PrimaryCount, floor: 1);
        return total;
    }
}
