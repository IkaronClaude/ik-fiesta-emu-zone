using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Parameter;
using Fiesta.Emu.Zone.Skill;

namespace Fiesta.Emu.Zone.Lua;

/// <summary>What a character of a class and level would be wearing and know, at its best.</summary>
/// <param name="ClassId">`ClassName.shn` ClassID.</param>
/// <param name="ClassName">`acEngName`, which is also the `Param&lt;Name&gt;Server.txt` stem.</param>
/// <param name="Equipment">One item per equip slot, the strongest the class may wear at this level.</param>
/// <param name="ItemIds">The chosen item ids, for the scorecard.</param>
/// <param name="Skills">Every skill the class knows at this level, highest rank per line.</param>
/// <param name="EmpowerPoints">`SkillPwrPt` from the class table — how many empower levels are available.</param>
public sealed record Loadout(
    int ClassId,
    string ClassName,
    IReadOnlyList<EquipmentPiece> Equipment,
    IReadOnlyList<int> ItemIds,
    IReadOnlyList<SkillDefinition> Skills,
    int EmpowerPoints)
{
    public IReadOnlyList<SkillDefinition> Offensive => [.. Skills.Where(s => s.IsOffensive)];
}

/// <summary>⭐ BEST-IN-SLOT FOR A CLASS AT A LEVEL, from the item tables — the matrix runs characters at
/// full kit so a bad score is the SCRIPT's fault and not the fixture's.
///
/// <para>Every earlier fixture in this project invented its gear ("a mace with MinWC 60-95 and a robe with
/// AC 40"), and that repeatedly produced conclusions about the bot which were really conclusions about a
/// character nobody would play — a level-40 Cleric dropped into a level-61 zone taking 639-746 per swing
/// against 879 max HP, kept alive only by a 2,000,000 HP crutch.</para></summary>
public static class LoadoutBuilder
{
    /// <summary>⚠️ <b>A weapon is not optional, and it is not just another slot.</b> With nothing in
    /// `Equip` 12 a character's WC comes entirely from the class table, which for a caster is close to
    /// zero — the difference between a wand and no wand is the difference between fighting and flailing.
    /// The builder therefore refuses to return a loadout with an empty weapon slot rather than quietly
    /// producing an unarmed character.</summary>
    public const int WeaponSlot = 12;

    /// <summary>How good an item is, for picking best-in-slot. Weighted the way the damage engine reads
    /// them: weapon damage first, then armour, then the magic pair — the same ordering
    /// `EquipmentCatalog.Score` uses, so a caster gets its wand and a fighter its blade.</summary>
    private static long Score(ItemDefinition i)
        => (long)i.MaxWc * 4 + i.Ac * 3 + i.MaxMa * 2 + i.ShieldAc + i.Th + i.Tb + i.CriRate;

    /// <summary>Build the strongest legal loadout for a class at a level.
    ///
    /// <para>An item qualifies when the class may wear it (`UseClassTypeInfo` — <b>never</b> a comparison
    /// against `UseClass`, where 1 means everyone and 0 means nobody) and its `DemandLv` is at or below the
    /// character's level. Best per `Equip` slot wins.</para></summary>
    public static Loadout? Build(EquipmentCatalog items, SkillCatalog skills, ClassParamTable table,
                                 string className, int level)
    {
        if (!skills.ClassIds.TryGetValue(className, out var classId)) return null;

        var best = new Dictionary<int, ItemDefinition>();
        foreach (var item in items.Items)
        {
            if (item.EquipSlot <= 0 || item.DemandLv > level) continue;
            if (!items.CanEquip(item, classId)) continue;

            // ⚠️ `LooksObtainable` exists for exactly this, and its own note predicted the failure: without
            // it "the best loadout for every class is the same handful of debug weapons, which makes class
            // differences vanish". Skipping it produced a level-45 HighCleric wielding 1503-1505 damage --
            // a flat range, the giveaway for a GM test item.
            if (!EquipmentCatalog.LooksObtainable(item)) continue;
            if (!best.TryGetValue(item.EquipSlot, out var held) || Score(item) > Score(held))
                best[item.EquipSlot] = item;
        }

        if (!best.ContainsKey(WeaponSlot)) return null;

        var chosen = best.Values.OrderBy(i => i.EquipSlot).ToList();
        return new Loadout(
            classId, className,
            [.. chosen.Select(i => i.ToPiece())],
            [.. chosen.Select(i => i.Id)],
            skills.LearnedBy(classId, level),
            table.At(level)?.SkillPwrPt ?? 0);
    }

    /// <summary>⭐ SPEND THE EMPOWER POINTS ON DAMAGE.
    ///
    /// <para>`SkillPwrPt` in the class table is how many empower levels a character has earned — 29 for a
    /// level-60 Enchanter. A `SkillEmpower` is four 4-bit fields (damage, SP, keep time, cool time) and
    /// `SkillEmpowerTable.DamageTerm` reads only the first, so a damage-focused build puts everything
    /// there, capped at <see cref="SkillEmpowerTable.MaxLevel"/> per skill.</para>
    ///
    /// <para>⚠️ The allocation belongs to a skill LINE and the wire reports it against the line's BASE
    /// rank — four of four allocations in the Fighter capture land on `SeverBone01`, `RedSlash01` and
    /// `PowerHit01` while the character casts ranks 04 and 05. Points are therefore spread across the
    /// offensive lines here, not stacked on one rank.</para></summary>
    public static IReadOnlyDictionary<int, SkillEmpower> AllocateEmpower(Loadout loadout)
    {
        var offensive = loadout.Offensive;
        var spend = new Dictionary<int, SkillEmpower>();
        if (offensive.Count == 0 || loadout.EmpowerPoints <= 0) return spend;

        var perSkill = new Dictionary<int, int>();
        var left = loadout.EmpowerPoints;
        while (left > 0)
        {
            var gave = false;
            foreach (var s in offensive)
            {
                if (left == 0) break;
                var at = perSkill.GetValueOrDefault(s.Id);
                if (at >= SkillEmpowerTable.MaxLevel) continue;
                perSkill[s.Id] = at + 1;
                left--;
                gave = true;
            }
            if (!gave) break;                                   // every line is at its cap
        }

        foreach (var (id, damage) in perSkill) spend[id] = SkillEmpower.From(damage);
        return spend;
    }
}
