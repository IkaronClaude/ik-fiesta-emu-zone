using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Combat;

/// <summary>Anything the damage formula can be run for — a player, a mob, a servant.
///
/// <para>Deliberately two members. The formula needs a level and a full stat container, and nothing else:
/// no position, no handle, no name. Keeping it that narrow is what lets the same calculator serve a
/// simulated character, a live bot's own character, and an emulated server object without any of them
/// knowing about each other.</para></summary>
public interface ICombatant
{
    /// <summary>Used for the attacker-vs-defender level gap, which is a term in its own right.</summary>
    int Level { get; }

    /// <summary>Every stat layer, not a flattened total — the formula reads several of them individually.
    /// See <see cref="ParameterContainer"/>.</summary>
    ParameterContainer Parameters { get; }

    // ---- what the ROLL phase asks the object, over and above its stats -------------------------------
    //
    // `roe_CalcDamage` really does need only a level and a container. `roe_HitRate`, `roe_CriticalRate`
    // and `SwingDamage` do not: they call back into `ShineObject` five times for things no cluster
    // holds. Each of these mirrors one of those calls and defaults to what a plain melee combatant with
    // no spent points and no item actions returns, so nothing that only computes damage has to care.

    /// <summary>`so_ply_FreeStatDex()->THRate` — the `unsigned short` at +1 of the six-byte
    /// `ShineCommonParameter::FreeStatDex` record (<c>{ Stat u8, THRate u16, TBRate u16, checksum u8 }</c>,
    /// read from the PDB). Added to <see cref="DamageCalculator.ToHitRating"/> at `roe_HitRate+0x1A`.
    ///
    /// <para><b>The Dex table has never been read.</b> `FreeStatStr`'s was, in full, out of a live zone —
    /// after a formula fitted to two samples proved wrong. Until the same is done for Dex, take this from
    /// the wire: 0x1035's <c>Aim</c> IS <c>roe_TH + THRate</c>, so a captured character's displayed Aim is
    /// the sum this and <c>roe_TH</c> make together.</para></summary>
    int FreeStatDexTHRate => 0;

    /// <summary>`so_ply_FreeStatDex()->TBRate` (the record's second `unsigned short`, at +3). Added to
    /// <see cref="DamageCalculator.ToBlockRating"/> at `roe_HitRate+0x51`; 0x1035's <c>Evasion</c> is the
    /// corresponding sum.</summary>
    int FreeStatDexTBRate => 0;

    /// <summary>`so_ply_FreeStatMen()->CriRate` — added by `roe_FreeStatCriRate` (0x004FFDB0).
    ///
    /// <para>Men, not Dex: the record that feeds criticals is the same eight-byte one that feeds magic
    /// resistance and max SP (<c>{ Stat, MRAbsolute, CriRate, MaxSP, checksum }</c>).</para></summary>
    int FreeStatMenCriRate => 0;

    /// <summary>`so_mobile_IsInMoving` (object vtable +0x610). Gates exactly one term in the whole engine:
    /// <see cref="ParameterContainer.PassiveMovingTbPlus"/>, added to the defender's block rating.</summary>
    bool IsInMoving => false;

    /// <summary>`so_AttackRange` (object vtable +0x510) — only ever compared against
    /// <see cref="DamageCalculator.RangedAttackThreshold"/>, so the default of
    /// <see cref="DamageCalculator.MeleeAttackRange"/> means "a melee swing".
    ///
    /// <para>`ShinePlayer`'s (0x00559930) is branchless and blunt: <b>450 for the Archer family, 100 for
    /// everyone else</b>, keyed on `cc_BaseClass` — Fighter 1, Cleric 6, Archer 11, Mage 16, Joker 21,
    /// Sentinel 26. `ShineMob`'s (0x005566C0) is the target's `so_BodySize` plus the selected weapon's
    /// range field, and 100003 when no weapon is selected. A skill overrides both with its own range.</para></summary>
    int AttackRange => DamageCalculator.MeleeAttackRange;

    /// <summary>`so_GetItemActionObserves` (object vtable +0x5D0) != null. When it is null the second
    /// shield-block roll and the second critical roll are skipped ENTIRELY — draw included, which matters
    /// because the draws come off one shared WELL512 stream.</summary>
    bool HasItemActionObserves => false;
}

/// <summary>A plain combatant, for tests and for callers with no game object to hang stats on.</summary>
public sealed class Combatant(int level, ParameterContainer parameters) : ICombatant
{
    public int Level { get; } = level;
    public ParameterContainer Parameters { get; } = parameters;

    /// <summary>A combatant whose base cluster holds the given stats and whose modifier layers are empty.</summary>
    public static Combatant FromBaseStats(int level, IEnumerable<KeyValuePair<Stat, int>> baseStats)
    {
        var p = new ParameterContainer();
        foreach (var (stat, value) in baseStats)
            p.Base[stat] = value;
        return new Combatant(level, p);
    }
}
