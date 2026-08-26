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
