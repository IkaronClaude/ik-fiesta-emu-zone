namespace Fiesta.Emu.Zone.Mob;

/// <summary>The per-mob state carried between ticks — the server's `MobActionArgument`.
///
/// <para>The current action pointer is at `MobActionArgument+0x320` in the original; here it is
/// <see cref="Current"/>.</para></summary>
public sealed class MobActionArgument
{
    public required IShineObject Actor { get; init; }
    public required MobTargetSelector Selector { get; init; }

    /// <summary>The state the mob is in. `MobActionArgument+0x320`.</summary>
    public MobActionBase Current { get; set; } = MobActionBase.Actor_Targetting;

    /// <summary>The handle the mob is currently attacking, or null. Set by `sm_SetTarget`.</summary>
    public IShineObject? Target { get; set; }

    /// <summary>Whether the mob is mid-movement. `so_mobile_StopHere` clears it.</summary>
    public bool Moving { get; set; }

    /// <summary>Everything the mob can currently perceive. In the server this comes from the axial-list
    /// scan; the simulator supplies it.</summary>
    public IReadOnlyList<IShineObject> Nearby { get; set; } = Array.Empty<IShineObject>();

    /// <summary>`ShineObject::so_CanSeeOtherObject`, which `MobActionTargetting::mab_Think` calls three
    /// times. It does NOT appear in acquisition, so engagement can be narrower than the detect circle —
    /// see docs/AGGRO.md. Defaults to always-visible until the real test is read.</summary>
    public Func<IShineObject, IShineObject, bool> so_CanSeeOtherObject { get; set; } = (_, _) => true;

    /// <summary>`ShineMobileObject::so_mobile_StopHere` (vtable +0xA48).</summary>
    public void so_mobile_StopHere() => Moving = false;

    /// <summary>`ShineMob::sm_SetTarget`.</summary>
    public void sm_SetTarget(IShineObject? target) => Target = target;
}

/// <summary>One state of the mob AI. The server's `MobTacticElement::MobActionBase`.
///
/// <para><b>`mab_Think` returns the NEXT state.</b> That is the entire driver: each tick, call think on
/// the current action and adopt whatever it hands back. The base implementation returns
/// `&amp;Actor::targetting` unconditionally.</para>
///
/// <para>States are static singletons in the original (`MobActionArgument::Actor::targetting` at
/// 0x0084CFBC and friends), which is mirrored here — a state holds no per-mob data, so one instance
/// serves every mob, exactly as the server does it.</para></summary>
public abstract class MobActionBase
{
    /// <summary>Advance one tick and return the state to be in next. Returning `this` stays put.</summary>
    public virtual MobActionBase mab_Think(MobActionArgument arg) => Actor_Targetting;

    /// <summary>Reaction to taking damage. The base is a genuine no-op — in the binary it folds onto
    /// 0x00549070, the universal empty body, so it is a stub and not a fallback worth calling.</summary>
    public virtual void mab_Damaged(MobActionArgument arg) { }

    // ---- the static instances, mirroring MobActionArgument::Actor ------------------------------------

    /// <summary>`Actor::base` @ 0x0084CFB8.</summary>
    public static readonly MobActionBase Actor_Base = new MobActionBaseDefault();

    /// <summary>`Actor::targetting` @ 0x0084CFBC.</summary>
    public static readonly MobActionBase Actor_Targetting = new MobActionTargetting();

    /// <summary>`Actor::roaming` @ 0x0084CFC4.</summary>
    public static readonly MobActionBase Actor_Roaming = new MobActionRoaming();

    private sealed class MobActionBaseDefault : MobActionBase;
}

/// <summary>`MobTacticElement::MobActionTargetting` — acquire a target, or fall back to roaming.
///
/// <para>The original calls `mts_TargetObject`, `sp_IsNormalAttack`, `sm_SetTarget` and
/// `so_CanSeeOtherObject` (three times), and returns `&amp;Actor::roaming` on one branch. This port covers
/// the acquire / cannot-see / nothing-found paths; the `sp_IsNormalAttack` branch is not yet read.</para></summary>
public sealed class MobActionTargetting : MobActionBase
{
    public override MobActionBase mab_Think(MobActionArgument arg)
    {
        var picked = arg.Selector.mts_SelectTarget(arg.Actor, arg.Nearby);

        // Line of sight is checked HERE and not during acquisition, so a target inside the detect circle
        // that cannot be seen is acquired and then discarded.
        if (picked is not null && !arg.so_CanSeeOtherObject(arg.Actor, picked))
            picked = null;

        arg.sm_SetTarget(picked);
        return picked is null ? Actor_Roaming : this;
    }
}

/// <summary>`MobTacticElement::MobActionRoaming` — idle wandering, re-checking for targets.</summary>
public sealed class MobActionRoaming : MobActionBase
{
    public override MobActionBase mab_Think(MobActionArgument arg)
        => arg.Selector.mts_SelectTarget(arg.Actor, arg.Nearby) is null ? this : Actor_Targetting;
}

/// <summary>`MobTacticElement::MobActionInMove_Cancelable` — a movement that being hit interrupts.
///
/// <para>Its `mab_Damaged` is two operations in the original: `so_mobile_StopHere()` then
/// `currentAction = &amp;Actor::targetting`. Being hit mid-move stops the mob dead and sends it back to
/// re-acquire.</para></summary>
public sealed class MobActionInMove_Cancelable : MobActionBase
{
    public override void mab_Damaged(MobActionArgument arg)
    {
        arg.so_mobile_StopHere();
        arg.Current = Actor_Targetting;
    }
}
