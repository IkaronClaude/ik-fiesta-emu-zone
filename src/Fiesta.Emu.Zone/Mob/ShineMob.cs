namespace Fiesta.Emu.Zone.Mob;

/// <summary>The mob itself — the parts of `ShineObjectClass::ShineMob` a combat simulation needs.
///
/// <para>`ShineMob` has 185 methods in the original; almost all of them are packet emission, persistence
/// and world bookkeeping. What is here is the combat surface.</para></summary>
public sealed class ShineMob : IShineObject
{
    public ushort Handle { get; init; }
    public int X { get; set; }
    public int Y { get; set; }
    public bool IsAlive { get; set; } = true;

    public MobTargetSelector Selector { get; } = new();

    /// <summary>`ShineMob::so_getDetectRange` — a `uint16` at the mob's data record +0x3B.</summary>
    public int so_getDetectRange
    {
        get => Selector.DetectRange;
        set => Selector.DetectRange = value;
    }

    /// <summary>The first thing that ever hit this mob, latched once.
    ///
    /// <para>`so_DamagedBy` keeps a `uint16` at mob+0x24AE seeded to 0xFFFF and writes the attacker's
    /// handle only while it is still 0xFFFF — so it records the FIRST attacker and is never overwritten
    /// while set, which is not the same as "current target" or "last attacker".</para></summary>
    public ushort FirstAttackerHandle { get; private set; } = NoAttacker;

    public const ushort NoAttacker = 0xFFFF;

    /// <summary>Damage contribution per attacker — `HitMeList::EnemyList`.
    ///
    /// <para>⚠️ This is NOT the aggro list. It exists to decide exp and loot rights
    /// (`el_ExpDistribute`, `el_FindLooter`) and has a different lifetime. Treating the two as one gives
    /// wrong target-switching AND wrong loot.</para></summary>
    public Dictionary<ushort, long> EnemyList { get; } = new();

    /// <summary>`ShineMob::so_DamagedBy(ShineObject* attacker, int damage, int aggroRatePermille, ...)`.
    ///
    /// <para>Ported from the call sequence at 0x00431750. The order matters — the aggro append happens
    /// BEFORE the damage is recorded for loot purposes, and the first-attacker latch after both.</para>
    ///
    /// <para>Not modelled here, and deliberately so: the mob bark (`mcm_DamageChat`), the boss-field
    /// automatic-action hook, and the <b>Lua script hook</b>. That last one matters — mob behaviour is
    /// scriptable, so a scripted mob can diverge from this simulation entirely.</para></summary>
    public void so_DamagedBy(IShineObject attacker, int damage, int aggroRatePermille)
    {
        Selector.mts_AppendAggroPoint(attacker, AggroFromDamage(damage, aggroRatePermille));

        // el_StoreDamage -- reward attribution, deliberately separate from aggro.
        EnemyList.TryGetValue(attacker.Handle, out var soFar);
        EnemyList[attacker.Handle] = soFar + damage;

        if (FirstAttackerHandle == NoAttacker)
            FirstAttackerHandle = attacker.Handle;
    }

    /// <summary>How much hate a hit generates: <c>damage * ratePermille / 1000</c>.
    ///
    /// <para>Read from `so_DamagedBy+0x147`: a 32-bit <c>imul</c> of damage by the rate, then the
    /// <c>0x10624DD3</c> / <c>sar 6</c> / sign-correct sequence, which is a signed divide by 1000
    /// truncating toward zero. The same idiom appears in `roe_LevelGapDamageRevision`.</para>
    ///
    /// <para><b>The multiply is 32-bit and wraps</b>, so this is written with <c>unchecked</c> int
    /// arithmetic rather than promoted to <c>long</c> — at a large enough damage-times-rate the server
    /// produces a wrapped, possibly negative, aggro value and so does this.</para></summary>
    public static int AggroFromDamage(int damage, int ratePermille)
        => unchecked(damage * ratePermille) / 1000;
}
