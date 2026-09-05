namespace Fiesta.Emu.Zone.Mob;

/// <summary>`MobTacticElement::MobAction2Region` (0x004BA1D0). Reached from MobActionChase when the mob
/// is farther from so_mob_LastHittedLocation than so_mob_ChaseRangeSquar allows. It runs one tick:
///
/// <list type="bullet">
///   <item>Return2Regen != 0 (2628 of 2878 mobs): mab_RunTo(so_mob_RegenLocation()), LastHittedLocation
///         := RegenLocation, -&gt; DuringReturn2Regen.</item>
///   <item>Return2Regen == 0 (250 mobs): both SHINE_XY_TYPEs := the current position,
///         so_mob_SetNextRoamingWaitTime, -&gt; Targetting.</item>
/// </list>
///
/// <para>It does not touch the aggro list. The only call it makes toward the target is
/// so_Mob_SetSendTagetInfo(0) (0x0042AC70), a broadcast flag, so Targetting re-acquires the same entry.
/// See MobTargetSelector.mts_Routine.</para></summary>
public sealed class MobAction2Region : MobActionBase
{
    public override MobActionBase mab_Think(MobActionArgument arg)
    {
        if (arg.Actor is not ShineMob self) return Actor_Targetting;

        arg.SendTargetInfo = false;

        if (arg.Combat.Return2Regen != 0)
        {
            arg.mab_RunTo(self.RegenLocation);
            // +0x16B: LastHittedLocation := RegenLocation before the walk starts, so the walk back is
            // not itself measured against so_mob_ChaseRangeSquar.
            self.LastHittedLocation = self.RegenLocation;
            return Actor_Return2Regen;
        }

        // +0xBA: both SHINE_XY_TYPEs are rewritten from the current position.
        self.RegenLocation = (self.X, self.Y);
        self.LastHittedLocation = (self.X, self.Y);
        arg.so_mob_SetNextRoamingWaitTime(arg.NowTenths + arg.Combat.RoamingRestTenths);
        return Actor_Targetting;
    }
}

/// <summary>`MobTacticElement::DuringReturn2Regen` (0x004B9010). Two branches and that is the whole
/// function: while so_mobile_IsInMoving() it returns `this`; when it stops it sets
/// so_mob_SetNextRoamingWaitTime(now + RoamingRestTime/100) and returns &amp;Actor::targetting.</summary>
public sealed class DuringReturn2Regen : MobActionBase
{
    public override MobActionBase mab_Think(MobActionArgument arg)
    {
        if (arg.so_mobile_IsInMoving()) return this;

        arg.so_mob_SetNextRoamingWaitTime(arg.NowTenths + arg.Combat.RoamingRestTenths);
        return Actor_Targetting;
    }
}
