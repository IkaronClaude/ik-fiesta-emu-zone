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

    /// <summary>`sm_CurrentTarget` (+0x24AE) — latched to the attacker's handle by `so_DamagedBy`, but
    /// ONLY while it is still 0xFFFF:
    /// <code>if (sm_CurrentTarget == 0xFFFF &amp;&amp; attacker) sm_CurrentTarget = attacker->handle;  // +0x1C4</code>
    ///
    /// <para>⚠️ The PDB's name and this behaviour disagree, and the name is not wrong — `so_DamagedBy` only
    /// SEEDS the field, so something else must clear it for "current" to be meaningful. That clearer has
    /// not been found, so within the damage path this behaves as "first attacker" and is named for the
    /// behaviour that was actually read. Do not assume it tracks the mob's live target.</para></summary>
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

    /// <summary>`sm_FamilyList` (+0x24B1) — the mobs this one is linked to.
    ///
    /// <para>A circular list in the original. It matters for aggro because
    /// <see cref="so_mob_DecreaseAggro"/> walks it and applies the same decrease to every member: hate shed
    /// by one family member is shed by all of them. Empty here unless a caller links a pack.</para></summary>
    public List<ShineMob> Family { get; } = new();

    /// <summary>`ShineMob::so_mob_AppendAggro` — vtable slot <c>+0x700</c>. Adds hate toward
    /// <paramref name="who"/> on THIS mob's list.
    ///
    /// <para>On a player the slot is the ICF-folded empty stub, which is the mechanical statement that
    /// <b>players have no hate list</b> — so this exists on the mob only, and there is nothing to mirror.</para></summary>
    public void so_mob_AppendAggro(IShineObject who, int points) => Selector.mts_AppendAggroPoint(who, points);

    /// <summary>`ShineMob::so_mob_DecreaseAggro` (0x0042D380) — vtable slot <c>+0x704</c>. Takes hate for
    /// <paramref name="who"/> off this mob's list AND off every family member's.
    ///
    /// <para>The family walk is the whole point of the function: 0x0042D380 loops over
    /// <c>sm_FamilyList</c> (+0x24B1) until it comes back to itself, calling `mts_DecreaseAggroPoint` on
    /// each member's selector. A lone mob is a one-element cycle, which is why the loop reads as
    /// pointless until a pack is linked.</para></summary>
    public void so_mob_DecreaseAggro(IShineObject who, int points)
    {
        Selector.mts_DecreaseAggroPoint(who, points);
        foreach (var member in Family)
            if (!ReferenceEquals(member, this))
                member.Selector.mts_DecreaseAggroPoint(who, points);
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
