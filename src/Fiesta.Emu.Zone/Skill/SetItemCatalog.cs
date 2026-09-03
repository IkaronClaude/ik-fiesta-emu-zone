using Fiesta.Emu.Zone.Data;

namespace Fiesta.Emu.Zone.Skill;

/// <summary>One row of `SetItemEffect.shn`, resolved — an `EffectDescription` in the server's terms.</summary>
/// <param name="Effect">`Effect`, the name `SetItem.shn` refers to it by. `EffectDescription.index`.</param>
/// <param name="Index">`Index` — which slot of the staging buffer it accumulates into.
/// `EffectDescription.seteffect`.</param>
/// <param name="Argument">`Argument`, in 1/1000. `EffectDescription.setargument`.</param>
/// <param name="Skills">The skill ids this effect applies to, or <c>null</c> for every skill.
/// `EffectDescription.skilllist` / `skillnumber` — and <b>null and empty mean opposite things</b>:
/// `siel_AppendEffect` skips the membership check entirely when the pointer is null, and rejects every
/// skill when the list exists but does not contain the id.</param>
public sealed record SetItemEffectDefinition(string Effect, SetIndex Index, int Argument,
                                             IReadOnlySet<int>? Skills)
{
    /// <summary>`siel_AppendEffect+0x91` — the bsearch against `skilllist`, or its absence.</summary>
    public bool AppliesTo(int skillId) => Skills is null || Skills.Contains(skillId);
}

/// <summary>⭐ Matched-equipment SET bonuses: which effects a character's gear grants, and what they do to
/// the skill they are casting.
///
/// <para>The server's chain, read end to end:</para>
///
/// <code>
/// sp_SetItemCheck (0x00510B50)                 // on any equipment change
///   memset(sic_ItemSetPiece, 0, 256)
///   for each cell of the EQUIPMENT bag (player+0x7FD8):
///       set = ItemDataBox[itemId]->SetItemIndex        // +0x10, resolved from ItemInfo.SetItemIndex
///       if (set &lt; sic_TotalNumber) sic_ItemSetPiece[set]++
///   sic_SetItemDefine(&amp;player.PlayerSetEffect)        // player+0x2A7E0
///
/// sic_SetItemDefine (0x00510370)
///   effectnumber = 0; effectarray[0..9] = 0xFFFF
///   for set in 0..255:
///       for pieces in 1..5:
///           if (pieces &gt; sic_ItemSetPiece[set]) break          // CUMULATIVE: 1..N all grant
///           for slot in 0..3:                                   // at most four per tier
///               handle = EffectByPiece[set].ebp_EffectArray[pieces][slot]
///               if (handle == 0xFFFF) break                     // first empty ends the tier
///               if (effectnumber == 10) { assert; break }       // at most ten in total
///               effectarray[effectnumber++] = handle
///
/// smo_ply_SetItemEffect(skillId) (0x00510D20)
///   se_Clear()                                                  // all 17 slots to 1000
///   for each handle: siel_AppendEffect(handle, skillId)
///
/// siel_AppendEffect(handle, skillId) (0x005104C0)
///   e = setitemeffectlist[handle]
///   if (e.skilllist &amp;&amp; !bsearch(skillId, e.skilllist, e.skillnumber)) return
///   se_Argument[e.seteffect] += e.setargument - 1000
/// </code>
///
/// <para><b>The bonus is cumulative and per-cast.</b> Four pieces of a set grant the 2-, 3- and 4-piece
/// effects together, and the whole buffer is rebuilt for every skill cast because the membership check is
/// against the skill being cast.</para>
///
/// <para>⚠️ <b>ONE LINK IS NOT READ: how `skilllist` is built from the row's `SkillGroup` / `From` / `To`.</b>
/// Nothing in `Zone.exe` that this project can find writes `setitemeffectlist`, so
/// <see cref="SkillsOf"/> is derived from the DATA rather than from the code. It resolves a group three
/// ways, and every one of the 235 rows but one lands in exactly one of them:</para>
///
/// <list type="number">
///   <item><b>Name plus rank</b> — 222 rows. `IceBolt` with `From`/`To` of `01`/`99` is
///         `IceBolt01`..`IceBolt99`, of which the ones that exist are the group.</item>
///   <item><b>An exact `InxName`</b> — 1 row. `PowerNorthBreeze01` already carries its rank.</item>
///   <item><b>A `SkillClassifier`</b> — 11 rows. Four-letter codes like `SBBF` and `HDMC` that appear in
///         `ActiveSkill.shn`'s `SkillClassifierA`/`B`/`C` columns.</item>
/// </list>
///
/// <para>That the three between them resolve 234 of 235 is the evidence for the rule, and it is evidence
/// rather than proof. The one that does not is `BrilliantlySP`, whose group `ShiningPurge` names no skill
/// and no classifier in this data — a dangling reference. It is treated as applying to NO skill, because
/// the alternative reading (a name nobody can resolve falling through to "every skill") would hand a
/// bonus to the entire game off a typo. See <see cref="SkillsOf"/>.</para></summary>
public sealed class SetItemCatalog
{
    /// <summary>`sic_SetItemDefine`'s inner bound — at most four effects per piece tier, and the first
    /// empty slot ends the tier.</summary>
    public const int EffectsPerTier = 4;

    /// <summary>`PlayerSetEffect.effectarray` is ten wide, and the eleventh effect is an assert rather
    /// than a resize.</summary>
    public const int MaxActiveEffects = 10;

    /// <summary>The highest piece count `sic_SetItemDefine` looks for: its inner loop runs 1..5.</summary>
    public const int MaxPieces = 5;

    private readonly Dictionary<string, SetItemEffectDefinition> _effects;
    private readonly List<(string Set, int Piece, string Effect)> _tiers;
    private readonly Dictionary<int, string> _setOfItem;

    private SetItemCatalog(Dictionary<string, SetItemEffectDefinition> effects,
                           List<(string, int, string)> tiers, Dictionary<int, string> setOfItem)
        => (_effects, _tiers, _setOfItem) = (effects, tiers, setOfItem);

    /// <summary>Every effect by name, as `SetItemEffect.shn` declares it.</summary>
    public IReadOnlyDictionary<string, SetItemEffectDefinition> Effects => _effects;

    /// <summary>Each item id's set name, from `ItemInfo.SetItemIndex`. Items in no set are absent.</summary>
    public IReadOnlyDictionary<int, string> SetOfItem => _setOfItem;

    /// <summary>⚠️ The membership rule, and the one part of this file derived from the data rather than
    /// read from the code. See the class remarks.</summary>
    /// <param name="group">`SetItemEffect.SkillGroup`.</param>
    /// <param name="from">`From`, the lowest rank. Non-numeric is treated as 1.</param>
    /// <param name="to">`To`, the highest. Non-numeric is treated as 99.</param>
    /// <param name="skillIdByName">`ActiveSkill.shn`'s `InxName` to id.</param>
    /// <param name="skillIdsByClassifier">Each `SkillClassifierA`/`B`/`C` value to the skills carrying
    /// it.</param>
    public static IReadOnlySet<int> SkillsOf(string group, string from, string to,
                                             IReadOnlyDictionary<string, int> skillIdByName,
                                             IReadOnlyDictionary<string, IReadOnlySet<int>> skillIdsByClassifier)
    {
        var low = int.TryParse(from, out var f) ? f : 1;
        var high = int.TryParse(to, out var t) ? t : 99;

        var byRank = new HashSet<int>();
        for (var rank = low; rank <= high; rank++)
            if (skillIdByName.TryGetValue(group + rank.ToString("00"), out var id))
                byRank.Add(id);
        if (byRank.Count > 0) return byRank;

        // A group that already carries its own rank names one skill.
        if (skillIdByName.TryGetValue(group, out var exact)) return new HashSet<int> { exact };

        if (skillIdsByClassifier.TryGetValue(group, out var byClassifier)) return byClassifier;

        // Resolves to nothing. Empty, NOT null: an unresolvable name must not fall through to "applies to
        // every skill". One row in the stock data lands here (`BrilliantlySP` / `ShiningPurge`).
        return new HashSet<int>();
    }

    /// <summary>Load `SetItem.shn`, `SetItemEffect.shn`, `ActiveSkill.shn` and `ItemInfo.shn` out of one
    /// `9Data/Shine` directory.</summary>
    public static SetItemCatalog Load(string shineDirectory)
    {
        var active = ShnFile.Load(Path.Combine(shineDirectory, "ActiveSkill.shn"));
        var skillIdByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var byClassifier = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var r in active.Rows)
        {
            var id = ShnFile.Int(r, "ID");
            skillIdByName[ShnFile.Str(r, "InxName")] = id;
            foreach (var column in new[] { "SkillClassifierA", "SkillClassifierB", "SkillClassifierC" })
            {
                if (!r.ContainsKey(column)) continue;
                var value = ShnFile.Str(r, column);
                if (string.IsNullOrEmpty(value) || value == "-") continue;
                (byClassifier.TryGetValue(value, out var set) ? set : byClassifier[value] = []).Add(id);
            }
        }
        var classifiers = byClassifier.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<int>)kv.Value,
                                                    StringComparer.Ordinal);

        var effects = new Dictionary<string, SetItemEffectDefinition>(StringComparer.Ordinal);
        foreach (var r in ShnFile.Load(Path.Combine(shineDirectory, "SetItemEffect.shn")).Rows)
        {
            var name = ShnFile.Str(r, "Effect");
            if (string.IsNullOrEmpty(name) || name == "-") continue;
            var group = ShnFile.Str(r, "SkillGroup");
            var skills = string.IsNullOrEmpty(group) || group == "-"
                ? null
                : SkillsOf(group, ShnFile.Str(r, "From"), ShnFile.Str(r, "To"), skillIdByName, classifiers);
            effects[name] = new SetItemEffectDefinition(name, (SetIndex)ShnFile.Int(r, "Index"),
                                                        ShnFile.Int(r, "Argument"), skills);
        }

        var tiers = new List<(string, int, string)>();
        foreach (var r in ShnFile.Load(Path.Combine(shineDirectory, "SetItem.shn")).Rows)
        {
            var effect = ShnFile.Str(r, "Effect");
            if (string.IsNullOrEmpty(effect) || effect == "-") continue;
            tiers.Add((ShnFile.Str(r, "Index"), ShnFile.Int(r, "Piece"), effect));
        }

        var setOfItem = new Dictionary<int, string>();
        foreach (var r in ShnFile.Load(Path.Combine(shineDirectory, "ItemInfo.shn")).Rows)
        {
            if (!r.ContainsKey("SetItemIndex")) break;
            var set = ShnFile.Str(r, "SetItemIndex");
            if (string.IsNullOrEmpty(set) || set == "-") continue;
            setOfItem[ShnFile.Int(r, "ID")] = set;
        }

        return new SetItemCatalog(effects, tiers, setOfItem);
    }

    /// <summary>`sp_SetItemCheck`'s first half — how many pieces of each set the character is wearing.
    /// Items in no set contribute nothing.</summary>
    public IReadOnlyDictionary<string, int> PiecesWorn(IEnumerable<int> equippedItemIds)
    {
        var worn = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in equippedItemIds)
            if (_setOfItem.TryGetValue(id, out var set))
                worn[set] = worn.GetValueOrDefault(set) + 1;
        return worn;
    }

    /// <summary>`sic_SetItemDefine` — the effects a character's worn pieces grant, cumulatively, in the
    /// order the server fills `PlayerSetEffect.effectarray`.
    ///
    /// <para>Both of its caps are modelled: at most <see cref="EffectsPerTier"/> per piece tier, and at
    /// most <see cref="MaxActiveEffects"/> in total — the eleventh is dropped, which on the server is an
    /// assert rather than a resize.</para></summary>
    public IReadOnlyList<SetItemEffectDefinition> ActiveEffects(IReadOnlyDictionary<string, int> piecesWorn)
    {
        var active = new List<SetItemEffectDefinition>();
        foreach (var (set, worn) in piecesWorn.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            for (var pieces = 1; pieces <= Math.Min(worn, MaxPieces); pieces++)
            {
                var tier = 0;
                foreach (var (_, _, effect) in _tiers.Where(t => t.Piece == pieces && t.Set == set))
                {
                    if (tier++ == EffectsPerTier) break;
                    if (active.Count == MaxActiveEffects) return active;
                    if (_effects.TryGetValue(effect, out var definition)) active.Add(definition);
                }
            }
        return active;
    }

    /// <summary>`smo_ply_SetItemEffect(skillId)` — the whole staging buffer for one cast, ready for
    /// <see cref="SkillBlastCascade"/>'s first step.</summary>
    public SetItemSkillEffect Stage(IEnumerable<int> equippedItemIds, int skillId)
        => Stage(PiecesWorn(equippedItemIds), skillId);

    /// <inheritdoc cref="Stage(System.Collections.Generic.IEnumerable{int},int)"/>
    public SetItemSkillEffect Stage(IReadOnlyDictionary<string, int> piecesWorn, int skillId)
        => SetItemSkillEffect.For(ActiveEffects(piecesWorn)
                                  .Where(e => e.AppliesTo(skillId))
                                  .Select(e => (e.Index, e.Argument)));
}
