using Fiesta.Emu.Zone.Lua;

namespace Fiesta.Emu.Zone.Parameter;

/// <summary>Giving the simulation's character a real class, level and kit instead of invented numbers.</summary>
public static class CharacterSheet
{
    /// <summary>Turn the simulated player into a character of a class at a level.
    ///
    /// <para>HP is computed by <see cref="CharacterParameters.MaxHp"/> — the class table's stored column
    /// plus five per spent Constitution point — not read out of a cluster slot, because `c_Storepure` never
    /// writes one.</para>
    ///
    /// <para>Swings now go through the real damage formula rather than a flat number: this sets
    /// <see cref="SimPlayer.UsesStatFormula"/>, and <c>DamageCalculator</c> reads the stat layers directly.</para>
    ///
    /// <para>It also carries over the class table's <c>JobChangeDmgUp</c> for this level — the multiplier
    /// every hit on a MONSTER goes through. It is 1000 for a base class and up to 2000 just after a job
    /// change, so a first-job character that does not get it deals half the damage it should.</para>
    ///
    /// <para>⚠️ Weapon damage is <b>gear only</b>. That is not a gap in the port: <c>CharClass::WC</c> is
    /// <c>xor eax, eax; ret 8</c> and no player class overrides it, so a player's base weapon value
    /// genuinely is zero. An unequipped character really does deal no weapon damage — give it a
    /// weapon.</para></summary>
    public static ParameterContainer Become(
        this SimPlayer player,
        ClassParamTable table,
        int level,
        FreeStats? freeStats = null,
        IEnumerable<EquipmentPiece>? equipment = null,
        Data.SkillCatalog? skills = null)
    {
        var container = CharacterParameters.Build(table, level, freeStats, equipment);
        var total = container.MakeTotal();

        player.Parameters = container;
        player.Level = level;
        player.MaxHp = CharacterParameters.MaxHp(table, level, total);
        player.Hp = player.MaxHp;
        player.MaxSp = CharacterParameters.MaxSp(table, level, total);
        player.Sp = player.MaxSp;
        player.UsesStatFormula = true;
        player.JobChangeDamageUpPermille = table.At(level)?.JobChangeDmgUp;

        // ⭐ SOUL STONES COME OUT OF THE CLASS TABLE, per class per level -- `SoulHP` is what one charge
        // restores and `MAXSoulHP` how many may be carried (`SoulSP`/`MAXSoulSP` likewise). Nothing here
        // is invented.
        //
        // The pairing is read off the data, not assumed: at level 1 an Enchanter has SoulHP 29 against a
        // MaxHP of 42 and MAXSoulHP 12; at level 60, SoulHP 872 against MaxHP 1245 and MAXSoulHP 170. The
        // first number tracks the HP pool and the second does not, which is what says which is the restore
        // and which the capacity. `MageDamageLvl60.pcapng` then shows the reserve being bought up one
        // charge at a time to 60, comfortably inside the 170 cap.
        if (table.At(level) is { } stones)
        {
            player.HpStoneRestore = stones.SoulHp;
            player.MaxHpStones = stones.MaxSoulHp;
            player.HpStones = stones.MaxSoulHp;      // a character that has just restocked
            player.HpStoneDepleted = false;

            player.SpStoneRestore = stones.SoulSp;
            player.MaxSpStones = stones.MaxSoulSp;
            player.SpStones = stones.MaxSoulSp;
            player.SpStoneDepleted = false;
        }

        // ⚠️ The class NAME here is the one on the parameter table -- `ParamEnchanterServer.txt` is
        // `Enchanter`, a job-changed class, and that is what makes the skill list a level-60 rotation
        // rather than a level-20 one. Naming a BASE class is legal and gets base-class skills; see
        // `SkillCatalog.LearnedBy`.
        if (skills is not null) player.LearnedSkills = skills.LearnedBy(table.ClassName, level);

        return container;
    }

    /// <summary>The effective stats — recomputed, because the layers may have changed since the last call.</summary>
    public static ParameterCluster EffectiveStats(this SimPlayer player)
        => (player.Parameters ?? new ParameterContainer()).MakeTotal();
}
