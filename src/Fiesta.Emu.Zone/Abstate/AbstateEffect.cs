using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Abstate;

/// <summary>One stat slot an action writes: which half, which direction, which slot.</summary>
/// <param name="Half">`Plus` is flat and `Rate` is permille with identity 1000 — a rate action of 200 is
/// +20%, not +200 points.</param>
/// <param name="Sign">+1 for `add`, -1 for `sub`.</param>
public readonly record struct AbstateStatWrite(StatHalf Half, int Sign, Stat Stat);

/// <summary>One second-tier field an action writes.</summary>
/// <param name="Sign">+1 for `add`, -1 for `sub`, and <b>0 for ASSIGN</b> — `SAA_HEALRATE` uses `mov`, so
/// zero here is a real operation and not a missing value.</param>
public readonly record struct AbstateFieldWrite(ContainerField Field, int Sign);

/// <summary>What one <see cref="SubAbstateAction"/> does to a `Parameter::Container`.</summary>
/// <param name="Flags">Behaviour bits set unconditionally.</param>
/// <param name="AltFlags">Behaviour bits set INSTEAD when the handler's conditional branch is taken. Only
/// `SAA_NOMOVE` has any, and the condition is the sub-state's type at +0x26 being 0x15 or 0x60.</param>
public sealed record AbstateEffect(
    IReadOnlyList<AbstateStatWrite> Stats,
    IReadOnlyList<AbstateFieldWrite> Fields,
    ContainerFlag Flags,
    ContainerFlag AltFlags);
