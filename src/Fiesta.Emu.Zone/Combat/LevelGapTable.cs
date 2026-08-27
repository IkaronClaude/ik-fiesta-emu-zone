using Fiesta.Emu.Zone.Data;

namespace Fiesta.Emu.Zone.Combat;

/// <summary>What kind of thing a combatant is, as `ShineObject`'s type virtual (vtable +0x4D0) reports it.
///
/// <para>`roe_LevelGapDamageRevision` branches on the PAIR of these to pick a table, so the two values it
/// actually tests are the only ones that need naming here.</para></summary>
public enum CombatantKind
{
    /// <summary>A player character. `roe_LevelGapDamageRevision+0x27` tests for 2.</summary>
    Player = 2,

    /// <summary>A monster. Tested for 5 at `+0x37`.</summary>
    Monster = 5,
}

/// <summary>`LevelGap_Player_to_Monster` / `LevelGap_Monster_to_Player` — the level-difference damage rate.
///
/// <para><b>This was the last approximation in the damage pipeline.</b> The port applied the rate at exactly
/// the right point (after the integer conversion, as a 32-bit multiply and a truncating divide by 1000) but
/// had no way to know what the rate WAS, so it defaulted to 1000 and said so. It is a table:
/// `DamageLvGapPVE.shn` for a player hitting a monster, `DamageLvGapEVP.shn` for the reverse.</para>
///
/// <para>The lookup is `LevelGap_Player_to_Monster::GetLevelCapRate` (0x005C8220), and it is a linear scan
/// rather than an index:</para>
/// <code>
/// int gap = defenderLevel - attackerLevel;            // +0x5/+0x8, and note the ORDER
/// for (unsigned i = 0; i &lt; GetTotal(); i++)
///     if (gap &lt;= (short)table[i].LvGap)            // movsx -- LvGap is SIGNED
///         return (unsigned short)table[i].DamageRate;
/// return 1000;                                        // +0x37, when no row matches
/// </code>
///
/// <para>Two things to get right. `LvGap` is a <b>signed</b> 16-bit field stored unsigned in the SHN, so
/// −150 reads as 65386 unless it is sign-extended — the first row of both tables is exactly that, a floor
/// row that only a 150-level gap could match. And the gap is <b>defender minus attacker</b>: a level 82
/// player attacking a level 61 Orc has a gap of −21, not +21, and −21 lands on the −10 row.</para>
///
/// <para>What it means in play, read off `DamageLvGapPVE`: a player hitting something at or above their own
/// level gets 1000, and the bonus ramps 1100 / 1200 / 1300 / 1400 over the first four levels of advantage,
/// then flattens at <b>1500</b> from five levels down. `DamageLvGapEVP` is <b>1000 in every one of its 24
/// rows</b> — a monster's damage is not adjusted for level at all, which the port had assumed and can now
/// state as a reading.</para></summary>
public sealed class LevelGapTable
{
    /// <summary>One row: the largest gap this rate applies to.</summary>
    public readonly record struct Row(int LvGap, int DamageRate);

    public required IReadOnlyList<Row> PlayerToMonster { get; init; }
    public required IReadOnlyList<Row> MonsterToPlayer { get; init; }

    /// <summary>The rate when no table applies — every pairing the original does not branch on, plus the
    /// null-argument guard at `roe_LevelGapDamageRevision+0x8`. Also `GetLevelCapRate`'s own fallthrough.</summary>
    public const int NoAdjustment = 1000;

    /// <summary>The rate for one attacker/defender pair, in permille.
    ///
    /// <para>Returns <see cref="NoAdjustment"/> for any pairing the original does not have a table for —
    /// monster-on-monster, for instance. Player-versus-player has a table (`DamageLvGapPVP.shn`) but it is a
    /// 151×150 matrix rather than a gap list and is not loaded here; PvP is out of scope for a mob
    /// simulation and inventing a lookup for it would be worse than the honest 1000.</para></summary>
    public int Rate(CombatantKind attacker, int attackerLevel, CombatantKind defender, int defenderLevel)
    {
        var rows = (attacker, defender) switch
        {
            (CombatantKind.Player, CombatantKind.Monster) => PlayerToMonster,
            (CombatantKind.Monster, CombatantKind.Player) => MonsterToPlayer,
            _ => null,
        };
        return rows is null ? NoAdjustment : Lookup(rows, defenderLevel - attackerLevel);
    }

    /// <summary>`GetLevelCapRate`'s scan: the first row whose `LvGap` is at least the gap, else 1000.</summary>
    public static int Lookup(IReadOnlyList<Row> rows, int gap)
    {
        foreach (var row in rows)
            if (gap <= row.LvGap)
                return row.DamageRate;
        return NoAdjustment;
    }

    public static LevelGapTable Load(string shineDirectory)
    {
        static List<Row> Read(string path)
        {
            var t = ShnFile.Load(path);
            var rows = new List<Row>();
            foreach (var r in t.Rows)
                // The SHN column is unsigned; the binary reads it with `movsx`. Without the cast every
                // negative gap becomes a huge positive one and the FIRST row matches everything.
                rows.Add(new Row((short)ShnFile.Int(r, "LvGap"), ShnFile.Int(r, "DamageRate")));
            return rows;
        }

        return new LevelGapTable
        {
            PlayerToMonster = Read(Path.Combine(shineDirectory, "DamageLvGapPVE.shn")),
            MonsterToPlayer = Read(Path.Combine(shineDirectory, "DamageLvGapEVP.shn")),
        };
    }
}
