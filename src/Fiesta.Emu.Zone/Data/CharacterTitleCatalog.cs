namespace Fiesta.Emu.Zone.Data;

/// <summary>One character-title tier's stat effect: an abstate applied at a strength.</summary>
/// <param name="TitleType">`Type` — which title group (`CHARACTER_TITLE_TYPE`).</param>
/// <param name="TitleLevel">`TitleLV` — the tier within that group.</param>
/// <param name="StateName">`StateName` — the abstate BY NAME, resolved through `as_FromName` the way the
/// passive path resolves its own. An empty name is a tier with no stat effect.</param>
/// <param name="Strength">`Strength` — the rank passed to the application.</param>
public sealed record CharacterTitleState(int TitleType, int TitleLevel, string StateName, int Strength);

/// <summary>`CharacterTitleStateServer.shn` — what a character TITLE actually grants.
///
/// <para>⚠️ This is the file an earlier pass here missed, and missing it produced a wrong conclusion.
/// `CharacterTitleData.shn` holds the title DEFINITIONS — names, kill/level thresholds, fame — and has no
/// stat columns, from which this port briefly concluded that titles have no combat effect. The stat side
/// lives HERE, in a second table, and the operator corrected it.</para>
///
/// <para>The server loads it into `CCharacterTitleDataStateServer`, whose `CT_DataState::StateData` is
/// <c>{ nStrength, pAbStateDic, pAbStateMainDic }</c>, and applies it through
/// `so_AbnormalState_BitSet` + `so_AbnormalState_BroadcastSet` at `sp_NC_MAP_LOGINCOMPLETE_CMD` (login)
/// and again at `sp_ReviveNow`.</para>
///
/// <para>⭐ So a title's bonus rides the ABSTATE channel, which is already ported — meaning a title can
/// grant critical rate the same way a crit scroll does, through
/// `AbnormalState.Rate[CriDamRate]`.</para></summary>
public sealed class CharacterTitleCatalog
{
    private readonly Dictionary<(int Type, int Level), CharacterTitleState> _byTier;

    private CharacterTitleCatalog(Dictionary<(int, int), CharacterTitleState> byTier) => _byTier = byTier;

    /// <summary>How many tiers carry a stat effect.</summary>
    public int Count => _byTier.Count;

    /// <summary>The abstate a title tier grants, or <c>null</c> when that tier has none.</summary>
    public CharacterTitleState? For(int titleType, int titleLevel)
        => _byTier.TryGetValue((titleType, titleLevel), out var s) ? s : null;

    public IEnumerable<CharacterTitleState> All => _byTier.Values;

    public static CharacterTitleCatalog Load(string shineDirectory)
    {
        var table = ShnFile.Load(Path.Combine(shineDirectory, "CharacterTitleStateServer.shn"));
        var byTier = new Dictionary<(int, int), CharacterTitleState>();

        foreach (var r in table.Rows)
        {
            var name = ShnFile.Str(r, "StateName");
            // A blank StateName is a tier with no stat effect -- real and common, not a parse failure.
            // Skipped so `For` returns null for it rather than an abstate nobody can resolve.
            if (string.IsNullOrWhiteSpace(name) || name == "-") continue;

            var row = new CharacterTitleState(
                TitleType: ShnFile.Int(r, "Type"),
                TitleLevel: ShnFile.Int(r, "TitleLV"),
                StateName: name,
                Strength: ShnFile.Int(r, "Strength"));

            byTier[(row.TitleType, row.TitleLevel)] = row;
        }

        return new CharacterTitleCatalog(byTier);
    }
}
