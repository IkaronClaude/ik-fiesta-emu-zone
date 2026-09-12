namespace Fiesta.Emu.Zone.Data;

/// <summary>One class in a line, and the level a character can first BE it.</summary>
public sealed record ClassTier(int ClassId, string Name, int FromLevel);

/// <summary>⭐ WHICH CLASS A CHARACTER OF A GIVEN LEVEL ACTUALLY IS.
///
/// <para>⚠️ This exists because the test matrix was running <b>a Ranger at level 25</b>. Ranger is
/// `ClassID` 15 -- the FIFTH class in the archer line and a level-100 job. A level-25 archer is a
/// HawkArcher, which is what the live bots are (`ArcherZero`, class 12, level 23). The matrix was
/// therefore handing level-25 characters the class tables and skill lists of jobs sixty to eighty levels
/// above them, and every per-class conclusion drawn from it was about a character that cannot exist.
/// Operator: "rangers cannot be lvl 25, that's a lvl 100 class".</para>
///
/// <para>It also makes the SP picture wrong in the direction the operator warned about -- "no char should
/// ever run out of sp really ... so long as characters only use skills OF THEIR LEVEL. If this simulation
/// erroneously uses higher level skills, mana will be gone in split seconds."</para>
///
/// <para><b>The tiers are DERIVED, not written down.</b> For each class, the level is the lowest non-zero
/// `uiDemandLv` among the skills that class may use and its predecessor may not -- the first thing you
/// could only do once you had the job. Every line agrees on 20 / 60 / 100:</para>
///
/// <code>
/// Fighter  -> CleverFighter 20 -> Warrior  60 -> Gladiator/Knight 100
/// Cleric   -> HighCleric    20 -> Paladin  60 -> Guardian         100
/// Archer   -> HawkArcher    20 -> Scout    60 -> Ranger           100
/// Mage     -> WizMage       20 -> Enchanter 60 -> Wizard          100
/// </code>
///
/// <para>⚠️ The minimum must skip zero. `uiDemandLv` 0 appears in the exclusive set for several classes
/// and a plain MIN returns it, which reads as "this job is available at level 0" -- CleverFighter and
/// Warrior both answered 0 before the zero was excluded.</para></summary>
public sealed class ClassProgression
{
    /// <summary>The four levelling lines, each as a chain of class ids in order. Which classes form a
    /// line is structural in `ClassName.shn` -- ids 1-5, 6-10, 11-15, 16-20 -- and the ORDER within each
    /// is the promotion order.</summary>
    public required IReadOnlyList<IReadOnlyList<ClassTier>> Lines { get; init; }

    /// <summary>The tier a character of this level is in, along the line whose base class is named.</summary>
    public ClassTier? ForLevel(string baseClassName, int level)
    {
        var line = Lines.FirstOrDefault(l => l.Count > 0
            && l[0].Name.Equals(baseClassName, StringComparison.OrdinalIgnoreCase));
        return line?.Where(t => t.FromLevel <= level).OrderByDescending(t => t.FromLevel).FirstOrDefault();
    }

    /// <summary>The class NAME a character of this level is, along the named line.</summary>
    public string? NameForLevel(string baseClassName, int level) => ForLevel(baseClassName, level)?.Name;

    /// <summary>Every base class, in id order -- the head of each line.</summary>
    public IEnumerable<string> BaseClasses => Lines.Where(l => l.Count > 0).Select(l => l[0].Name);

    public static ClassProgression Derive(SkillCatalog skills, IReadOnlyList<string> lineOrder)
    {
        var lines = new List<IReadOnlyList<ClassTier>>();

        foreach (var chain in Chains(skills, lineOrder))
        {
            var tiers = new List<ClassTier>();
            for (var i = 0; i < chain.Count; i++)
            {
                var (id, name) = chain[i];
                if (i == 0) { tiers.Add(new ClassTier(id, name, 1)); continue; }

                // The first thing this job can do that the one before it could not.
                var prev = chain[i - 1].Id;
                var level = skills.Skills
                    .Where(s => s.DemandLevel > 0 && skills.UsableBy(s, id) && !skills.UsableBy(s, prev))
                    .Select(s => s.DemandLevel)
                    .DefaultIfEmpty(0)
                    .Min();
                if (level <= 0) continue;              // no exclusive skill: not a tier we can place
                tiers.Add(new ClassTier(id, name, level));
            }
            if (tiers.Count > 0) lines.Add(tiers);
        }

        return new ClassProgression { Lines = lines };
    }

    /// <summary>Walk each named line, resolving its class ids. A line is given as the base class followed
    /// by its promotions in order; the ids come from `ClassName.shn` through the catalog.</summary>
    private static IEnumerable<List<(int Id, string Name)>> Chains(
        SkillCatalog skills, IReadOnlyList<string> lineOrder)
    {
        List<(int, string)>? current = null;
        foreach (var name in lineOrder)
        {
            if (name.Length == 0)                      // blank separates one line from the next
            {
                if (current is { Count: > 0 }) yield return current;
                current = null;
                continue;
            }
            if (!skills.ClassIds.TryGetValue(name, out var id)) continue;
            (current ??= []).Add((id, name));
        }
        if (current is { Count: > 0 }) yield return current;
    }
}
