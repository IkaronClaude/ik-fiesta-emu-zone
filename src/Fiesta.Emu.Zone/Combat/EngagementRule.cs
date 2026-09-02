using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Combat;

/// <summary>Which set of combat rules governs a hit — the server's eight
/// <c>RulesOfEngagement</c> singletons.
///
/// <para><c>roe_CalcDamage</c> is not overridden by any of them: one virtual pipeline on the base class
/// drives every rule and only the components it calls differ. So a rule changes <b>which stats feed attack
/// and defence</b>, and <b>how hit and critical are decided</b> — never the core damage formula, which was
/// measured identical across all eight.</para></summary>
public enum EngagementRule
{
    /// <summary>An ordinary weapon swing. <c>roe_normalPY</c>.</summary>
    NormalPhysical,

    /// <summary>A physical skill. <c>roe_physical</c>. Same stats as <see cref="NormalPhysical"/>.</summary>
    PhysicalSkill,

    /// <summary>An ordinary magic attack. <c>roe_normalMA</c>. Driven by Int and MAmin/MAmax, defended by
    /// magic resistance rather than armour.</summary>
    NormalMagic,

    /// <summary>A magical skill. <c>roe_magical</c>. Same stats as <see cref="NormalMagic"/>.</summary>
    MagicalSkill,

    /// <summary>A cure skill. <c>roe_cure</c>. Overrides hit rate only.</summary>
    CureSkill,

    /// <summary>Cannot miss. <c>roe_always</c>.</summary>
    AlwaysHit,

    /// <summary>Always critical. <c>roe_alwaysCritical</c>.</summary>
    AlwaysCritical,

    /// <summary>A healing "attack". <c>roe_alwaysHealAttack</c>.</summary>
    HealAttack,
}

/// <summary>Whether a rule draws on the physical or the magical stat set.</summary>
public enum DamageSchool
{
    /// <summary>Attack from Str + WCmin..WCmax, defended by armour class (Con + AC).</summary>
    Physical,

    /// <summary>Attack from Int + MAmin..MAmax, defended by magic resistance (Men + MR).</summary>
    Magical,
}

public static class EngagementRuleExtensions
{
    /// <summary>The stat set a rule draws on. Measured on one input (attacker Str 300 Int 250 WCmin 400
    /// WCmax 600 MAmin 350 MAmax 550, defender Con 150 Men 170 AC 120 MR 110): the physical rules give
    /// attack 800 / defend 270, the magical ones 700 / 280.</summary>
    public static DamageSchool School(this EngagementRule rule) => rule switch
    {
        EngagementRule.NormalMagic or EngagementRule.MagicalSkill => DamageSchool.Magical,
        _ => DamageSchool.Physical,
    };

    /// <summary>Whether the rule forces every hit to be a critical, regardless of the rolled chance.</summary>
    public static bool AlwaysCriticals(this EngagementRule rule) => rule == EngagementRule.AlwaysCritical;

    /// <summary>Whether this rule's `roe_Damage` adds the FREE-STAT terms — and which pair.
    ///
    /// <para>`roe_Damage` is <b>overridden per rule</b> at vtable slot 4, which this port missed for a
    /// long time: it had the base function and treated it as the whole story. The two overrides call the
    /// base and then add a flat pair (`roe_Damage@NormalPY+0x2F3`):</para>
    /// <code>
    /// damage = base(arg, attack, defend) + attacker-&gt;FreeStat&lt;A&gt;() - defender-&gt;FreeStat&lt;D&gt;();
    /// </code>
    ///
    /// <para>Physical reads <c>FreeStatStr</c> / <c>FreeStatCon</c> (slots +0x468 / +0x474); magical reads
    /// <c>FreeStatInt</c> / <c>FreeStatMen</c> (+0x46C / +0x478) — the governing stats of each school.</para>
    ///
    /// <para>Which rules use which, read off the vtables rather than assumed:</para>
    /// <list type="bullet">
    ///   <item>NormalPY, PhisycalSkill and <b>AlwaysCritical</b> share the physical override</item>
    ///   <item>NormalMA and MagicalSkill share the magical one</item>
    ///   <item>CureSkill, AlwaysHit and HealAttack keep the BASE — no free-stat term at all</item>
    /// </list>
    ///
    /// <para>AlwaysCritical taking the physical override is the one that would not have been guessed.</para></summary>
    public static DamageSchool? FreeStatSchool(this EngagementRule rule) => rule switch
    {
        EngagementRule.NormalPhysical or EngagementRule.PhysicalSkill or EngagementRule.AlwaysCritical
            => DamageSchool.Physical,
        EngagementRule.NormalMagic or EngagementRule.MagicalSkill => DamageSchool.Magical,
        _ => null,
    };

    /// <summary>Whether attack power is scaled by weapon mastery.
    ///
    /// <para>Every rule does EXCEPT <see cref="EngagementRule.MagicalSkill"/>, which ignores it entirely.
    /// Measured with both mastery rates swept 0 to 4000 over the same weapon bounds: normal physical,
    /// physical skill and normal magic all scale linearly (0 gives 0, 2000 doubles), while a magical skill
    /// stays flat at 151.0 throughout. So a mage with no mastery still lands full skill damage, but their
    /// plain attack does nothing.</para>
    ///
    /// <para>The asymmetry is real and was measured, not inferred from the symmetry of the other three.</para></summary>
    public static bool AppliesWeaponMastery(this EngagementRule rule) => rule != EngagementRule.MagicalSkill;

    /// <summary>Whether the rule's `roe_AttackPower` reads the cast skill's `ActiveSkillInfo` row.
    ///
    /// <para>Only the two SKILL rules do. `roe_AttackPower@PhisycalSkill` (0x00506B40) and
    /// `@MagicalSkill` (0x00506ED0) dereference <c>arg-&gt;sklinfo-&gt;sdi_Activ</c>; `@NormalPY` and
    /// `@NormalMA` never touch it, and the remaining four rules do not override `roe_AttackPower` at
    /// all.</para>
    ///
    /// <para>So passing a skill row alongside a plain-swing rule is not an error and is not silently
    /// half-applied — it is ignored, exactly as the server ignores it.</para></summary>
    public static bool ReadsSkillRow(this EngagementRule rule)
        => rule is EngagementRule.PhysicalSkill or EngagementRule.MagicalSkill;
}
