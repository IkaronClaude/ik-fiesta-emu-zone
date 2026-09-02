namespace Fiesta.Emu.Zone.Skill;

/// <summary>`ActiveSkillInfo` — the CLIENT skill row (`ActiveSkill.shn`), reached through
/// `SkillDataIndex::sdi_Activ` (+0x04).
///
/// <para><b>This, not `ActiveSkillInfoServer`, is what the damage path reads.</b> The plan expected
/// a skill's damage bonus to come from `ActiveSkillInfoServer`'s `DmgIncRate` / `DmgIncValue`, and it
/// does not: `roe_AttackPower@PhisycalSkill` (0x00506B40) and `@MagicalSkill` (0x00506ED0) dereference
/// `arg-&gt;sklinfo-&gt;sdi_Activ` and read four columns out of THIS struct. `sdi_ServInf` is read by the HIT
/// path instead — see <see cref="ActiveSkillInfoServer"/>.</para>
///
/// <para>The four columns, at the offsets the two functions use:</para>
/// <code>
/// physical   MinWC +0xDB   MinWCRate +0xDF   MaxWC +0xE3   MaxWCRate +0xE7
/// magical    MinMA +0xEB   MinMARate +0xEF   MaxMA +0xF3   MaxMARate +0xF7
/// </code>
///
/// <para>Each bound gets its own flat term and its own permille rate, and the two bounds are scaled
/// INDEPENDENTLY — a skill can widen its own damage range, not just shift it:</para>
/// <code>
/// low  = low  + low  * MinRate/1000 + MinFlat;
/// high = high + high * MaxRate/1000 + MaxFlat;
/// </code>
///
/// <para>All four are loaded UNSIGNED (each `fild` is followed by a sign test and an add of 2^32), so a
/// column above 2^31 is a large positive number rather than a negative one.</para></summary>
/// <param name="MinFlat">`MinWC` / `MinMA` — added to the low bound after its rate.</param>
/// <param name="MinRatePermille">`MinWCRate` / `MinMARate` — scales the low bound, in 1/1000.</param>
/// <param name="MaxFlat">`MaxWC` / `MaxMA` — added to the high bound after its rate.</param>
/// <param name="MaxRatePermille">`MaxWCRate` / `MaxMARate` — scales the high bound, in 1/1000.</param>
/// <param name="Empower">`nT0`..`nT3`, the per-cast empower damage table. Null for a skill that carries
/// no empower table; <see cref="SkillEmpowerTable.DamageTerm"/> then contributes nothing.</param>
public sealed record ActiveSkillInfo(
    uint MinFlat = 0,
    uint MinRatePermille = 0,
    uint MaxFlat = 0,
    uint MaxRatePermille = 0,
    SkillEmpowerTable? Empower = null)
{
    /// <summary>A skill row that changes nothing — the identity, for testing the plain-swing path through
    /// the skill signature.</summary>
    public static ActiveSkillInfo Neutral { get; } = new();
}

/// <summary>`ActiveSkillInfoServer` — the SERVER skill row, reached through `SkillDataIndex::sdi_ServInf`
/// (+0x00). 78 bytes; only the columns below reach the damage engine.
///
/// <para><b>`roe_HitRate` is the only damage-path reader.</b> `RulesOfEngagementPhisycalSkill::roe_HitRate`
/// (0x00502D30) and its magical twin (0x00503360) read exactly two columns, and the second one chooses
/// between two entirely different accuracy formulas — see <see cref="SkillHitType"/>. Nothing in
/// `roe_AttackPower`, `roe_DefendPower`, `roe_CriticalRate` or `roe_CalcDamage` touches this struct.</para>
///
/// <para>The rest of the row is real and is read elsewhere in the server — `AggroPerDamage` /
/// `AbsoluteAggro` by the aggro list, `SwingTime` / `HitTime` by the action timing, `AddSoul` by the soul
/// counter. They are deliberately absent here rather than modelled as zero, because this type describes
/// what the DAMAGE engine consumes.</para></summary>
/// <param name="HitRate">`SkilPyHitRate` (+0x23) for a physical skill, `SkilMaHitRate` (+0x27) for a
/// magical one. Loaded unsigned. It replaces the plain swing's hard-coded 850 in the accuracy ratio, so it
/// is in the same units: a skill with 850 here is exactly as accurate as an ordinary attack.</param>
/// <param name="HitType">`SkillHitType` (+0x3A), the formula selector.</param>
public sealed record ActiveSkillInfoServer(uint HitRate, SkillHitType HitType = SkillHitType.AsNormal)
{
    /// <summary>The row a plain swing behaves like: the normal accuracy formula with the 850 the
    /// hard-coded path uses.</summary>
    public static ActiveSkillInfoServer LikeANormalSwing { get; } = new(850);
}

/// <summary>`SkillHitType` — ⭐ <b>it selects the skill's RULES OBJECT, and the accuracy formula is a
/// side effect.</b>
///
/// <para>An earlier reading here had it as a two-value flag, from `roe_HitRate@PhisycalSkill` testing it
/// against zero. That test is real, but it is not what the field is FOR. `sdb_Load@SkillDataBox`
/// (0x00585F40 +0x2CD) reads `sdi_ServInf->SkillHitType` and jumps through a six-entry table at
/// 0x00587CC8 to assign `sdi_DamageRule`:</para>
///
/// <code>
/// 0 -> roe_physical    1 -> roe_magical    2 -> roe_always
/// 3 -> roe_cure        4 -> roe_cure       5 -> roe_always
/// </code>
///
/// <para>Measured over all 2,791 rows of `ActiveSkillInfoServer.shn`: 1,293 physical, 538 magical,
/// 948 always, 3+9 cure. So a skill's whole rule — which stats it draws on, how it hits, whether it can
/// miss — comes from this one column.</para>
///
/// <para>⚠️ Which makes the `!= 0` branch inside `roe_HitRate@PhisycalSkill` largely UNREACHABLE for
/// skills loaded this way: a non-zero value routes the skill to a different rule object, whose own
/// `roe_HitRate` is a different function. The branch is in the binary and the port keeps it, but do not
/// expect `SkillHitType` 1..5 to arrive there.</para>
///
/// <para>Verified against the Fighter capture: all nine damaging skills are type 0 (physical) and the two
/// non-damaging ones — `MoraleDecrease04`, `SnearShout02` — are type 2 (always), which is exactly the
/// split the wire's `isdamage` flag showed independently. Every mage skill is type 1.</para>
///
/// <para>The original note follows, since the accuracy behaviour it describes is still correct.</para>
///
/// <para>Which accuracy formula a skill uses. The names are the ones the server's own
/// debug output prints (` As Normal ` at 0x006D06EC, ` As Skill ` at 0x006D06E0), which is how the two
/// branches were told apart.
///
/// <para>The distinction is real and large: one formula is the ordinary aim-versus-evasion contest, the
/// other ignores both stats entirely and reads only the two LEVELS. A skill of the second kind cannot be
/// dodged by stacking evasion, and cannot be landed more often by stacking aim.</para></summary>
public enum SkillHitType
{
    /// <summary>Zero — the ordinary contest, `hit = HitRate * toHit / toBlock`. Identical in shape to a
    /// plain swing, with the skill's own <see cref="ActiveSkillInfoServer.HitRate"/> where the swing has
    /// its 850.</summary>
    AsNormal = 0,

    /// <summary>Non-zero — `hit = HitRate * attackerLevel / defenderLevel`. Aim, evasion and every
    /// modifier feeding them drop out completely.
    ///
    /// <para>⚠️ The branch is `!= 0`, not `== 1`: the server tests the column against zero and takes this
    /// path for ANY other value. Modelled as a single member for that reason — a hypothetical `2` would
    /// behave identically, and inventing distinct members would imply a distinction the code does not
    /// make.</para></summary>
    ByLevelRatio = 1,
}
