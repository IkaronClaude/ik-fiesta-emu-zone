using Fiesta.Emu.Zone.Data;

namespace Fiesta.Emu.Zone.Parameter;

/// <summary>Building a mob's stats, mirroring what the zone does.</summary>
public static class MobParameters
{
    /// <summary>`Parameter::Container::c_StoreMob` (0x0043C550) — fill a mob's base cluster from its
    /// `MobInfoServer` row.
    ///
    /// <para>Structurally the twin of <see cref="CharacterParameters.StorePure"/>: seed from the plus
    /// eraser, write the five primaries, write the defences, then the same tail of 1000 into MoveSpeed,
    /// HPRecover and SPRecover followed by zeroing slots 22..31.</para>
    ///
    /// <para><b>The Con/Dex crossover is here too.</b> `MobInfoServer` declares Str, Dex, Con, Int, Men in
    /// that order (+0x50, +0x52, +0x54, +0x56, +0x58) but the cluster's order is Str, Con, Dex, Int, Men, so
    /// the original reads +0x54 into slot 1 and +0x52 into slot 2. Copying the file's order straight across
    /// would silently swap every mob's Constitution and Dexterity.</para>
    ///
    /// <para><b>WCmin, WCmax, TH, MAmin, MAmax and MH are written as ZERO here</b>, exactly as the original
    /// does. They are filled in later, when a weapon is selected — see <see cref="PrepareWeapon"/>.</para></summary>
    public static void StoreMob(ParameterContainer container, MobInfoServer info)
    {
        var b = container.Base;

        b[Stat.Str] = info.Str;
        b[Stat.Con] = info.Con;
        b[Stat.Dex] = info.Dex;
        b[Stat.Int] = info.Int;
        b[Stat.Men] = info.Men;

        // Defences. The gaps are deliberate: the original writes zero into WCmin/WCmax (+0x14/+0x18),
        // TH (+0x20), MAmin/MAmax (+0x28/+0x2C) and MH (+0x34) rather than skipping them.
        b[Stat.WCmin] = 0;
        b[Stat.WCmax] = 0;
        b[Stat.AC] = info.Ac;
        b[Stat.TH] = 0;
        b[Stat.TB] = info.Tb;
        b[Stat.MAmin] = 0;
        b[Stat.MAmax] = 0;
        b[Stat.MR] = info.Mr;
        b[Stat.MH] = 0;
        b[Stat.MB] = info.Mb;

        b[Stat.MoveSpeed] = CharacterParameters.BaseUnitySlotValue;
        b[Stat.HPRecover] = CharacterParameters.BaseUnitySlotValue;
        b[Stat.SPRecover] = CharacterParameters.BaseUnitySlotValue;
        foreach (var slot in new[]
                 {
                     Stat.CastingTime, Stat.Critical, Stat.PhisycalWeaponMastery, Stat.MagicalWeaponMastery,
                     Stat.ShieldAC, Stat.HitRate, Stat.EvaRate, Stat.MACri, Stat.CriDam, Stat.MagCriDam,
                 })
            b[slot] = 0;
    }

    /// <summary>`ShineMob::sm_PrepareWeapon` (0x004A9D50) — stage a chosen weapon's values into the mob's
    /// stat container.
    ///
    /// <para><b>A mob's weapon is its GEAR.</b> The original writes the six values to
    /// <c>mob + 0x10A0 … 0x10C0</c>, and `ShineMobileObject::smo_Param` — the embedded
    /// <see cref="ParameterContainer"/> — sits at <c>+0x0FC0</c>. The difference is <c>0xCC</c>, which is the
    /// <see cref="StatModifier.Item"/> Plus cluster, and every one of the six lands exactly on its
    /// `WCmin`/`WCmax`/`TH`/`MAmin`/`MAmax`/`MH` slot.</para>
    ///
    /// <para>That single fact explains the whole shape of mob combat, and it is worth stating plainly
    /// because this project got it wrong first:</para>
    /// <list type="bullet">
    ///   <item><see cref="StoreMob"/> zeroes those slots at spawn because a mob has not picked a weapon yet.</item>
    ///   <item>Selecting a weapon (`so_mob_SelectWeapon` → `so_mob_SkillParameterSet_WeaponIndex` →
    ///         `sm_PrepareWeapon`) writes it into the Item layer.</item>
    ///   <item>`c_MakeTotal` folds Item.Plus into the total as its second operation, so the weapon is simply
    ///         there by the time anything reads the stats.</item>
    ///   <item>Which is why `roe_MinWC` has <b>no mob branch</b> — it reads the container and finds the
    ///         weapon already in it, exactly as it finds a player's sword.</item>
    /// </list>
    ///
    /// <para>The consequence for this simulation is that mob damage no longer needs a special path: a mob is
    /// an <see cref="Combat.ICombatant"/> whose gear happens to come from `MobWeapon`, and the defender's AC
    /// applies to it like anything else.</para>
    ///
    /// <para>⚠️ The original takes its arguments as <c>(MaxWC, MinWC, TH, MaxMA, MinMA, MH)</c> — the WC and
    /// MA pairs are passed max-first. Getting that backwards swaps every mob's damage bounds.</para></summary>
    public static void PrepareWeapon(ParameterContainer container, Data.MobWeapon weapon)
    {
        var item = container.Plus(StatModifier.Item);
        item[Stat.WCmin] = weapon.MinWc;
        item[Stat.WCmax] = weapon.MaxWc;
        item[Stat.TH] = weapon.Th;
        item[Stat.MAmin] = weapon.MinMa;
        item[Stat.MAmax] = weapon.MaxMa;
        item[Stat.MH] = weapon.Mh;
    }

    /// <summary>`ShineMob::so_mob_Regenerate+0x543` — which <see cref="Combat.EngagementRule"/> a mob's
    /// NORMAL ATTACK resolves through, decided ONCE at spawn from `MobWeapon.HitType`.
    ///
    /// <para>This is the reader `HitType` was missing. Every mobile object is constructed pointing at
    /// <c>roe_normalPY</c> (<c>ShineMobileObject::ShineMobileObject+0xC4</c> writes
    /// <c>smo_RulesOfNormalAttack = &amp;roe_normalPY</c>), and a mob then overwrites it at regeneration
    /// with this branch:</para>
    /// <code>
    /// const MobDataBoxIndex* box = mob->sm_MobDataBox;      // +0x1F90
    /// if (box->weapon == NULL)                     roe = &amp;roe_normalPY;   // +0x54E
    /// else if (box->weapon[0].weapon == NULL)      roe = &amp;roe_normalMA;   // +0x553  (see below)
    /// else if (box->weapon[0].weapon->HitType)     roe = &amp;roe_normalMA;   // +0x557, HitType at +0x6D
    /// else                                         roe = &amp;roe_normalPY;
    /// </code>
    ///
    /// <para>Three things in that are worth stating because none is what you would guess:</para>
    /// <list type="bullet">
    ///   <item><b>Weapon index 0 decides for the whole mob.</b> The rule is chosen at spawn from row 0 and
    ///         never revisited, so a mob with a magical row further down the list still swings physically.
    ///         It is the same index <c>mab_Think</c> forces for a player target — see
    ///         <see cref="Data.MobDataBox.AttackAgainstPlayer"/>.</item>
    ///   <item><b>The test is <c>!= 0</c>, not <c>== HT_MA</c>.</b> `HT_NONE` (2) also selects the magical
    ///         rule, and that is NOT a corner case — <b>708 of the 2,834 mobs with a weapon row carry
    ///         `HT_NONE` at row 0</b>. 629 of them are not fightable at all and the other 79 are props
    ///         (`KQ_Gate1`, `C_PillarofLight`, `GuildStone`) whose row 0 is WC 0-0 / MA 0-0, so nothing
    ///         swings differently for it today. Narrowing the test to `== HT_MA` would still be wrong, and
    ///         checking rather than assuming is the only reason that count is known.</item>
    ///   <item><b>A null row 0 selects MAGIC, not physical</b> — the counterintuitive middle branch. It
    ///         cannot be reached from `MobWeapon.shn`, because a parsed row always has a weapon, so
    ///         <paramref name="weapons"/> being empty is the FIRST branch (no array at all → physical).
    ///         The middle case is kept in the comment rather than the code because there is no honest way
    ///         to express "present but null" here.</item>
    /// </list>
    ///
    /// <para>⚠️ This governs the whole swing, not just its stat set: `smo_SwingDamage` calls
    /// <c>roe_HitRate</c> (slot 6), <c>roe_HitRateByGlobalAction</c> (slot 11) and <c>roe_CalcDamage</c>
    /// (slot 7) through this pointer. A magical mob is also unblockable — `RulesOfEngagementNormalMA`
    /// leaves <c>roe_ShieldBlock</c> at the base return-0 stub, where `NormalPY` overrides it.</para>
    ///
    /// <para>What it changes in practice: of the 2,059 fightable mobs with a weapon row, <b>334 attack
    /// magically</b> — measured against the defender's MR instead of AC, and unblockable. `GhostKnight`
    /// (WC 153-234 against MA 2234-3402) is the shape of the difference.</para></summary>
    public static Combat.EngagementRule NormalAttackRule(IReadOnlyList<Data.MobWeapon> weapons)
        => weapons.Count > 0 && weapons[0].HitType != Data.HitType.Physical
            ? Combat.EngagementRule.NormalMagic
            : Combat.EngagementRule.NormalPhysical;

    /// <summary>`CharClassMob::MaxHP` (0x004496F0) — a mob's maximum HP.
    ///
    /// <para>Two instructions of substance: it calls a virtual to get the mob's info record and reads
    /// <c>[+0x46]</c>, which the PDB names `MobInfo::MaxHP`. <b>It never touches the stat cluster</b> — unlike
    /// the player's version, there is no Constitution term. A mob's HP is simply what the table says.</para></summary>
    public static int MaxHp(MobInfo info) => info.MaxHp;
}

/// <summary>A mob as the damage formula sees it: a level and a stat container, built from game data.
///
/// <para>This is the link the simulation was missing. Mobs previously carried an all-zero container, so any
/// attack against them was resolved against a defenceless target.</para></summary>
public sealed class MobCombatant : Combat.ICombatant
{
    public required MobInfo Info { get; init; }
    public required MobInfoServer Server { get; init; }

    /// <summary>The attack this mob uses against a PLAYER — weapon index 0, which is the index
    /// `mab_Think` forces when the target casts to `ShinePlayer`.
    ///
    /// <para>Kept beside the container rather than inside it, because the binary keeps it that way: nothing
    /// folds `MobWeapon` into a stat cluster. Its <c>MinWc</c>/<c>MaxWc</c> are the mob's attack values and
    /// its <c>SwingTime</c>/<c>HitTime</c> are the swing and damage-landing timings.</para></summary>
    public Data.MobWeapon? NormalAttack { get; init; }

    /// <summary>Which rules of engagement this mob's normal attack resolves through, from
    /// <see cref="MobParameters.NormalAttackRule"/>. Physical for all but the casters.</summary>
    public Combat.EngagementRule NormalAttackRule { get; init; } = Combat.EngagementRule.NormalPhysical;

    /// <summary>`sm_GetWeaponCastTime` for this mob's anti-player attack — the third term of
    /// `nextAttackAt`. Zero for every mob in the current data, because weapon row 0 names no skill.</summary>
    public int NormalAttackCastTimeMs { get; init; }

    public required ParameterContainer Parameters { get; init; }

    /// <summary>From `MobInfo`, not from the cluster — mobs have no level column in their stat block.</summary>
    public int Level => Info.Level;

    /// <summary>The targeting policy this mob's `EnemyDetectType` selects.
    ///
    /// <para>⚠️ The three rarer values — `ED_AGGRESSIVE2`, `ED_AGGREESIVEALL` and `ED_ENEMYALLDETECT`, 22
    /// mobs between them — have their own `mts_SelectTarget` overrides that have NOT been read. They are
    /// mapped to <see cref="Mob.TargetingPolicy.Aggressive"/> because they all derive from it, which is a
    /// reasonable floor and not a claim about what they add.</para></summary>
    public Mob.TargetingPolicy Policy => Server.DetectType switch
    {
        Data.EnemyDetect.NoBrain => Mob.TargetingPolicy.NoBrain,
        Data.EnemyDetect.Bout => Mob.TargetingPolicy.Bout,
        _ => Mob.TargetingPolicy.Aggressive,
    };

    public int MaxHp => MobParameters.MaxHp(Info);

    /// <summary>Build a mob's combat identity from the joined tables.</summary>
    public static MobCombatant? Build(MobDataBox box, string inxName, Data.SkillDataBox? skills = null)
    {
        var info = box.InfoFor(inxName);
        var server = box.ServerFor(inxName);
        if (info is null || server is null) return null;

        var container = new ParameterContainer();
        MobParameters.StoreMob(container, server);

        // Staging the weapon into the Item layer is what the server does on weapon selection, and it is what
        // makes a mob a full combatant rather than one with an empty attack.
        var weapon = box.AttackAgainstPlayer(inxName);
        if (weapon is not null)
            MobParameters.PrepareWeapon(container, weapon);

        return new MobCombatant
        {
            Info = info,
            Server = server,
            NormalAttack = weapon,
            // The rule comes from the WHOLE weapon list, not from `weapon`: the server's branch
            // distinguishes "no weapon array" from "array whose row 0 is null", and only the list can
            // tell those apart.
            NormalAttackRule = MobParameters.NormalAttackRule(box.WeaponsFor(inxName)),
            NormalAttackCastTimeMs = weapon is null
                ? 0
                : (skills ?? Data.SkillDataBox.Empty).CastTimeMs(weapon),
            Parameters = container,
        };
    }
}
