using Fiesta.Emu.Zone.Lua;

namespace Fiesta.Emu.Zone.Parameter;

/// <summary>Giving the simulation's character a real class, level and kit instead of invented numbers.</summary>
public static class CharacterSheet
{
    /// <summary>Turn the simulated player into a character of a class at a level.
    ///
    /// <para>HP comes from the combined total, which for HP means the class table's stored <c>MaxHP</c>
    /// column plus whatever the gear and buff layers contribute — there is no HP curve being modelled.</para>
    ///
    /// <para>⚠️ Attack damage is taken from the total's <see cref="Stat.WCmax"/>, which today means
    /// <b>gear only</b>: the base weapon-class slots are filled by per-class virtual methods that have not
    /// been read, so an unequipped character has a WCmax of zero and would deal no damage. That is the
    /// honest consequence of the gap rather than a bug — see docs/PARAMETERS.md. Pass equipment, or set
    /// <see cref="SimPlayer.AttackDamage"/> yourself, until those virtuals are ported.</para></summary>
    public static ParameterContainer Become(
        this SimPlayer player,
        ClassParamTable table,
        int level,
        FreeStats? freeStats = null,
        IEnumerable<EquipmentPiece>? equipment = null)
    {
        var container = CharacterParameters.Build(table, level, freeStats, equipment);
        var total = container.MakeTotal();

        player.Parameters = container;
        player.Level = level;
        player.MaxHp = total[Stat.MaxHP];
        player.Hp = player.MaxHp;
        player.AttackDamage = total[Stat.WCmax];
        return container;
    }

    /// <summary>The effective stats — recomputed, because the layers may have changed since the last call.</summary>
    public static ParameterCluster EffectiveStats(this SimPlayer player)
        => (player.Parameters ?? new ParameterContainer()).MakeTotal();
}
