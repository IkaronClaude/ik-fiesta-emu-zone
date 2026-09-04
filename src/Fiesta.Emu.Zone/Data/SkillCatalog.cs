using Fiesta.Emu.Zone.Skill;

namespace Fiesta.Emu.Zone.Data;

/// <summary>One skill, joined across the four tables that describe it.
///
/// <para>⚠️ <b>No single file holds a skill.</b> `ActiveSkill.shn` has the damage columns, the SP cost and
/// the timings; `ActiveSkillView.shn` has the LEVEL REQUIREMENT and nothing else the engine needs;
/// `ActiveSkillInfoServer.shn` has the accuracy columns and the rules-object selector. A loader that reads
/// one of them produces a skill that cannot be cast, cannot be learned, or cannot hit.</para></summary>
/// <param name="Id">`ID` — the number the wire carries and `bot.cast` takes.</param>
/// <param name="InxName">The internal name, e.g. `PowerHit05`. The rank is its trailing digits.</param>
/// <param name="Name">The displayed name.</param>
/// <param name="DemandLevel">`uiDemandLv` from `ActiveSkillView.shn` — the character level at which this
/// rank becomes learnable. <b>The only table that carries it.</b></param>
/// <param name="UseClass">Index into the `UseClassTypeInfo.shn` matrix; resolve with
/// <see cref="SkillCatalog.UsableBy"/> rather than comparing it to a class id.</param>
/// <param name="Sp">SP the cast costs.</param>
/// <param name="Range">Cast range in world units. 0 for a self-cast.</param>
/// <param name="CastTimeMs">`CastTime` — the cast animation, during which movement cancels the cast.</param>
/// <param name="CooldownMs">`DlyTime` — the recharge before the skill may be cast again.</param>
/// <param name="UsableDegree">The facing arc the target must be inside.</param>
/// <param name="LandsOn">`Last` — which side the skill affects: 0 enemy, 1 self, 2 party, 3 ally.</param>
/// <param name="EffectType">5 is a heal, which is how the bot categorises healing without matching names.</param>
/// <param name="Physical">⚠️ `Skill.ActiveSkillInfo`, NOT the `Data.ActiveSkillInfo` in this same
/// namespace — two types carry this game struct's name and they model different halves of one row.
/// The `MinWC`/`MaxWC` damage columns, for the physical-skill rules object.</param>
/// <param name="Magical">The `MinMA`/`MaxMA` damage columns, for the magical one.</param>
/// <param name="Server">The accuracy row — `SkilPyHitRate`/`SkilMaHitRate` and the hit type.</param>
/// <param name="HealAmount">⭐ HOW MUCH A HEAL HEALS, from `SpecialValueA` where `SpecialIndexA` is
/// <see cref="Skill.SkillSpecial.HealAmount"/>.
///
/// <para>⚠️ It is NOT in the damage columns, and looking for it there is why a Cleric's heal did nothing:
/// `Heal10` carries `MinMA`/`MaxMA` of ZERO and its 1,100 sits in `SpecialValueA`. The value scales with
/// rank the way you would expect — `Heal01` 110, `Heal10` 1,100, `GreatHeal04` 1,800 — which is what
/// confirms the reading.</para></param>
public sealed record SkillDefinition(
    int Id,
    string InxName,
    string Name,
    int DemandLevel,
    int UseClass,
    int Sp,
    int Range,
    int CastTimeMs,
    int CooldownMs,
    int UsableDegree,
    int LandsOn,
    int EffectType,
    Skill.ActiveSkillInfo Physical,
    Skill.ActiveSkillInfo Magical,
    ActiveSkillInfoServer Server,
    int HealAmount = 0)
{
    /// <summary>⭐ WHICH RULES OBJECT THIS SKILL GOES THROUGH — read off the data, not off the class.
    ///
    /// <para>A skill carries damage columns in one half of the row or the other: `MinWC`/`MaxWC` for a
    /// physical skill, `MinMA`/`MaxMA` for a magical one. A Cleric's `HolyStrike` is physical and its
    /// `Heal` is neither; an Enchanter's `MagicBall` is magical. Choosing by the CASTER's class would put
    /// every Cleric skill through the physical rules and get the healers' damage wrong.</para></summary>
    public bool IsMagical => Magical != Skill.ActiveSkillInfo.Neutral && Physical == Skill.ActiveSkillInfo.Neutral;

    /// <summary>`EffectType == 5` is the client's own "a heal was applied" marker, and it is what the bot
    /// categorises healing by — a name match would miss every heal whose name is not "Heal".</summary>
    public bool IsHeal => EffectType == 5;

    /// <summary>A skill that lands on an enemy and carries damage columns in either half.</summary>
    public bool IsOffensive => LandsOn == 0 && (Physical != Skill.ActiveSkillInfo.Neutral || Magical != Skill.ActiveSkillInfo.Neutral);

    /// <summary>The trailing digits of <see cref="InxName"/> — `PowerHit05` is rank 5. 0 when the name
    /// carries no rank.</summary>
    public int Rank
    {
        get
        {
            var i = InxName.Length;
            while (i > 0 && char.IsAsciiDigit(InxName[i - 1])) i--;
            return i < InxName.Length && int.TryParse(InxName.AsSpan(i), out var r) ? r : 0;
        }
    }

    /// <summary>`PowerHit05` → `PowerHit`. Skills of one line share this.</summary>
    public string Line => Rank == 0 ? InxName : InxName[..^CountTrailingDigits(InxName)];

    private static int CountTrailingDigits(string s)
    {
        var n = 0;
        while (n < s.Length && char.IsAsciiDigit(s[^(n + 1)])) n++;
        return n;
    }
}

/// <summary>Every active skill in the game, joined and indexed.
///
/// <para>⚠️ <b>This exists because the skill reader lived in the TEST.</b> `BucketGroundTruthTests` grew a
/// private `ActiveSkill.shn` loader while proving the damage engine against a capture, which meant the
/// simulation — the thing that has to actually CAST — could not reach a single skill row. The bot spent
/// its runs meleeing: a level-60 Enchanter hits an Orc for 20 with its wand and needs 154 swings, which
/// looks like a broken damage engine and is really a caster with no spells.</para></summary>
public sealed class SkillCatalog
{
    public required IReadOnlyList<SkillDefinition> Skills { get; init; }

    /// <summary>`UseClass` value to the set of ClassIDs allowed to cast it — the same
    /// `UseClassTypeInfo.shn` matrix the equipment catalog resolves against.</summary>
    public required IReadOnlyDictionary<int, HashSet<int>> UseClassAllows { get; init; }

    /// <summary>`acEngName` to `ClassID`, from `ClassName.shn` — so a scenario can name a class the way
    /// the class parameter table does (`ParamClericServer.txt` → `Cleric`) rather than carrying an id.</summary>
    public required IReadOnlyDictionary<string, int> ClassIds { get; init; }

    private Dictionary<int, SkillDefinition>? _byId;

    /// <summary>⚠️ FIRST ROW WINS, because the shipped data contains a genuine DUPLICATE ID. `KQRoboAtkSk2`
    /// and `GoMasterAtkSkill2` both claim id 9034, in all three tables consistently, so one of them is
    /// unreachable by id no matter who loads it. Which one the real client keeps is not knowable from the
    /// data; it does not matter here because both carry <c>uiDemandLv 0</c> and are therefore not
    /// learnable, so <see cref="LearnedBy"/> cannot return either. A plain `ToDictionary` throws on it,
    /// which is how it was found.</summary>
    public SkillDefinition? Find(int id)
        => (_byId ??= FirstWins(Skills, s => s.Id)).GetValueOrDefault(id);

    private static Dictionary<int, T> FirstWins<T>(IEnumerable<T> rows, Func<T, int> key)
    {
        var d = new Dictionary<int, T>();
        foreach (var r in rows) d.TryAdd(key(r), r);
        return d;
    }

    /// <summary>⚠️ <c>UseClass == 1</c> is EVERYONE and <c>0</c> is NOBODY — the same trap as the item
    /// matrix, and the reason this is a lookup rather than a comparison.</summary>
    public bool UsableBy(SkillDefinition skill, int classId)
        => UseClassAllows.TryGetValue(skill.UseClass, out var allowed) && allowed.Contains(classId);

    /// <summary>⭐ THE SKILLS A CHARACTER OF THIS CLASS AND LEVEL WOULD KNOW — the simulation's stand-in
    /// for the zone-login skill list the server sends.
    ///
    /// <para>Live, `bot.learnedSkills()` is whatever the server said; there is no client-side rule. Here
    /// there is no server, so the set is derived the way a player would fill it in: every rank the class
    /// may cast whose <c>uiDemandLv</c> it has reached, keeping only the HIGHEST rank of each line —
    /// because a character who has trained `PowerHit05` does not go on casting `PowerHit01`.</para>
    ///
    /// <para>This is derived from game data, never a baked list of ids.</para></summary>
    /// <summary>⭐ NAME THE JOB-CHANGED CLASS, NOT THE BASE ONE. A level-60 "Fighter" knows three
    /// offensive skills, all rank 02, because a real character stopped being a Fighter at level 20: the
    /// ranks above belong to `CleverFighter` and then `Warrior`. Measured across the whole table — base
    /// classes get 14-16 skills at level 60, their promotions 22-30.
    ///
    /// <para>Returns nothing for a class name the game does not have, which is a refusal: silently
    /// falling back to the base class would hand a level-60 character a level-20 rotation and make the
    /// simulation's damage look like an engine bug.</para></summary>
    public IReadOnlyList<SkillDefinition> LearnedBy(string className, int level)
        => ClassIds.TryGetValue(className, out var id) ? LearnedBy(id, level) : [];

    public IReadOnlyList<SkillDefinition> LearnedBy(int classId, int level)
        => [.. Skills
            .Where(s => s.DemandLevel > 0 && s.DemandLevel <= level && UsableBy(s, classId))
            .GroupBy(s => s.Line)
            .Select(g => g.MaxBy(s => s.Rank)!)
            .OrderBy(s => s.Id)];

    /// <summary>⚠️ <b>THE JOIN SPANS BOTH DATA TREES, AND NEITHER ONE IS ENOUGH.</b> Checked, not assumed:
    /// <code>
    /// ActiveSkill.shn            client YES   server YES
    /// ActiveSkillView.shn        client YES   server NO    &lt;- the level requirement
    /// ActiveSkillInfoServer.shn  client NO    server YES   &lt;- the accuracy columns
    /// UseClassTypeInfo.shn       client YES   server YES
    /// </code>
    ///
    /// <para>That split is the real client/server data boundary, not an accident of these trees: a client
    /// has no business knowing a skill's hit rate, and a server has no business knowing its icon. The
    /// simulation plays both halves, so it may read both — but each table is read from the side that
    /// actually ships it.</para></summary>
    /// <param name="shineDirectory">Server data, e.g. `Z:/ServerSource/9Data/Shine`.</param>
    /// <param name="ressystemDirectory">Client data, e.g. `Z:/ClientProd2/ressystem`. When null the level
    /// requirements are unknown and <see cref="LearnedBy"/> returns NOTHING rather than guessing — a
    /// refusal, since the alternative is handing a level-1 character the whole skill tree.</param>
    public static SkillCatalog Load(string shineDirectory, string? ressystemDirectory = null)
    {
        var active = ShnFile.Load(Path.Combine(shineDirectory, "ActiveSkill.shn"));
        var matrix = ShnFile.Load(Path.Combine(shineDirectory, "UseClassTypeInfo.shn"));
        var classNames = ShnFile.Load(Path.Combine(shineDirectory, "ClassName.shn"));

        var viewPath = ressystemDirectory is null ? null
            : Path.Combine(ressystemDirectory, "ActiveSkillView.shn");
        var view = viewPath is not null && File.Exists(viewPath) ? ShnFile.Load(viewPath) : null;

        var serverPath = Path.Combine(shineDirectory, "ActiveSkillInfoServer.shn");
        var server = File.Exists(serverPath) ? ShnFile.Load(serverPath) : null;

        static int I(IReadOnlyDictionary<string, object> r, string c) => ShnFile.Int(r, c);
        static string S(IReadOnlyDictionary<string, object> r, string c) => ShnFile.Str(r, c);

        // Column i after `UseClass` is ClassID i+1 -- the matrix is in ClassID order.
        var classColumns = matrix.Columns
            .Where(c => !c.Name.Equals("UseClass", StringComparison.OrdinalIgnoreCase))
            .Select((c, index) => (c.Name, ClassId: index + 1))
            .ToList();

        var allows = new Dictionary<int, HashSet<int>>();
        foreach (var r in matrix.Rows)
        {
            var set = new HashSet<int>();
            foreach (var (column, classId) in classColumns)
                if (I(r, column) != 0) set.Add(classId);
            allows[I(r, "UseClass")] = set;
        }

        // First row wins on the duplicate id 9034 -- see `Find`.
        var demandLv = new Dictionary<int, int>();
        foreach (var r in view?.Rows ?? []) demandLv.TryAdd(I(r, "ID"), I(r, "uiDemandLv"));
        var serverRows = FirstWins(server?.Rows ?? [], r => I(r, "ID"));

        var skills = new List<SkillDefinition>(active.Rows.Count);
        foreach (var r in active.Rows)
        {
            var id = I(r, "ID");

            // ⭐ `nT0`..`nT3` -- the four empower tables, five entries each. Only the first of each run is
            // named; the other four are `UndefinedN` in file order. The runs are DAMAGE / SP / KEEPTIME /
            // COOLTIME, matching SKILL_EMPOWER's four bitfields.
            uint[] Run(string first, params string[] rest)
                => [(uint)I(r, first), .. rest.Select(c => (uint)I(r, c))];
            var empower = SkillEmpowerTable.FromArrays(
                Run("nT0", "Undefined3", "Undefined4", "Undefined5", "Undefined6"),
                Run("nT1", "Undefined7", "Undefined8", "Undefined9", "Undefined10"),
                Run("nT2", "Undefined11", "Undefined12", "Undefined13", "Undefined14"),
                Run("nT3", "Undefined15", "Undefined16", "Undefined17", "Undefined18"));

            // All four columns are loaded UNSIGNED by the engine (each `fild` is followed by a sign test
            // and an add of 2^32), so a column above 2^31 is a large positive number, not a negative one.
            var physical = new Skill.ActiveSkillInfo(
                MinFlat: (uint)I(r, "MinWC"), MinRatePermille: (uint)I(r, "MinWCRate"),
                MaxFlat: (uint)I(r, "MaxWC"), MaxRatePermille: (uint)I(r, "MaxWCRate"),
                Empower: empower);
            var magical = new Skill.ActiveSkillInfo(
                MinFlat: (uint)I(r, "MinMA"), MinRatePermille: (uint)I(r, "MinMARate"),
                MaxFlat: (uint)I(r, "MaxMA"), MaxRatePermille: (uint)I(r, "MaxMARate"),
                Empower: empower);

            // A row with no damage columns at all is neither physical nor magical -- carrying the empower
            // table alone would make `IsMagical` true for every buff in the file.
            static bool Blank(Skill.ActiveSkillInfo a)
                => a.MinFlat == 0 && a.MinRatePermille == 0 && a.MaxFlat == 0 && a.MaxRatePermille == 0;
            if (Blank(physical)) physical = Skill.ActiveSkillInfo.Neutral;
            if (Blank(magical)) magical = Skill.ActiveSkillInfo.Neutral;

            var isMagical = physical == Skill.ActiveSkillInfo.Neutral && magical != Skill.ActiveSkillInfo.Neutral;
            var srv = serverRows.TryGetValue(id, out var sr)
                ? new ActiveSkillInfoServer(
                    HitRate: (uint)I(sr, isMagical ? "SkilMaHitRate" : "SkilPyHitRate"),
                    HitType: (SkillHitType)I(sr, "SkillHitType"))
                : ActiveSkillInfoServer.LikeANormalSwing;

            skills.Add(new SkillDefinition(
                id, S(r, "InxName"), S(r, "Name"),
                DemandLevel: demandLv.GetValueOrDefault(id),
                UseClass: I(r, "UseClass"),
                Sp: I(r, "SP"),
                Range: I(r, "Range"),
                CastTimeMs: I(r, "CastTime"),
                CooldownMs: I(r, "DlyTime"),
                UsableDegree: I(r, "UsableDegree"),
                LandsOn: I(r, "Last"),
                EffectType: I(r, "EffectType"),
                physical, magical, srv,
                // The heal amount rides the SPECIAL slots, not the damage columns. Only slot A is read:
                // no heal in the file carries HealAmount anywhere else.
                HealAmount: I(r, "SpecialIndexA") == (int)Skill.SkillSpecial.HealAmount
                            ? I(r, "SpecialValueA")
                            : 0));
        }

        var classIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in classNames.Rows)
        {
            var id = I(r, "ClassID");
            var name = S(r, "acEngName");
            if (id > 0 && name.Length > 1) classIds.TryAdd(name, id);
        }

        return new SkillCatalog { Skills = skills, UseClassAllows = allows, ClassIds = classIds };
    }
}
