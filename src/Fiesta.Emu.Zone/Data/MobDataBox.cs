namespace Fiesta.Emu.Zone.Data;

/// <summary>`MobType` — what kind of entity a "mob" actually is.
///
/// <para><b>Not everything in a spawn table is an enemy.</b> Gathering nodes (herbs, wood, mines) and
/// scenery are spawned by exactly the same `MobRegen` machinery as monsters, so a simulation that treats
/// every row as a fightable enemy will have its character swinging at mushrooms it can never kill.</para></summary>
public enum MobType
{
    Human = 0, MagicLife, Spirit, Beast, Elemental, Undead,
    Npc, Object, Mine, Herb, Wood, NoName, NoTarget, NoTarget2,
}

/// <summary>`EnemyDetect` — which `MobTargetSelector` subclass a mob uses to pick targets.
///
/// <para>This is not a flag about detection <em>range</em>; it selects the whole targeting POLICY, and the
/// values line up one-for-one with the RTTI hierarchy under `MobTargetSelector`.</para>
///
/// <para><b>220 mobs are <see cref="Bout"/> — passive.</b> They acquire nothing on their own and only
/// retaliate. Slime, MushRoom, Imp and Crab are all in that set, which matches how the early game plays.
/// A simulation that gives every mob the same selector has those 220 attacking on sight.</para></summary>
public enum EnemyDetect
{
    /// <summary>`ED_BOUT` — `MobTargetBout`. Passive: retaliates, never initiates.</summary>
    Bout = 0,
    /// <summary>`ED_AGGRESSIVE` — `MobTargetAggresive`. The common case (1,872 mobs).</summary>
    Aggressive = 1,
    /// <summary>`ED_NOBRAIN` — `MobTargetNoBrain`. Shopkeepers and other non-combatants (764).</summary>
    NoBrain = 2,
    /// <summary>`ED_AGGRESSIVE2` — `MobTargetAggresive2`.</summary>
    Aggressive2 = 3,
    /// <summary>`ED_AGGREESIVEALL` — `MobTargetAggresiveALL` (the server's spelling).</summary>
    AggressiveAll = 4,
    /// <summary>`ED_ENEMYALLDETECT`.</summary>
    EnemyAllDetect = 5,
}

/// <summary>`NORMALHITTYPE` — whether an attack is resolved as physical or magical.</summary>
public enum HitType
{
    /// <summary>`HT_PY` — physical; measured against the defender's AC.</summary>
    Physical = 0,
    /// <summary>`HT_MA` — magical; measured against MR.</summary>
    Magical = 1,
    /// <summary>`HT_NONE`.</summary>
    None = 2,
}

/// <summary>`MobInfo` — the client-visible half of a mob's definition.
///
/// <para>Field names and the order below are the PDB's `MobInfo` struct. <see cref="MaxHp"/> sits at +0x46,
/// which is exactly what `CharClassMob::MaxHP` (0x004496F0) reads: it ignores the stat cluster entirely and
/// returns this number.</para></summary>
public sealed record MobInfo(
    int Id, string InxName, string Name,
    int Level, int MaxHp,
    int WalkSpeed, int RunSpeed,
    bool IsNpc, int Size,
    MobType Type)
{
    /// <summary>Whether this is something a character can actually fight.
    ///
    /// <para>Gathering nodes and scenery are excluded: <see cref="MobType.Herb"/>, <see cref="MobType.Wood"/>,
    /// <see cref="MobType.Mine"/>, <see cref="MobType.Object"/> and the two no-target kinds, plus anything
    /// flagged <see cref="IsNpc"/>.</para>
    ///
    /// <para>This matters more than it sounds: <b>ten of Uruga's twenty spawn types are gathering nodes</b> —
    /// `MUSHROOM7/8/9`, `HERB7/8/9`, `WOOD7/8/9` and a present box. The level-2 enemy called `MushRoom` is a
    /// different entry entirely and does not appear in that map.</para></summary>
    public bool IsFightable =>
        !IsNpc && Type is not (MobType.Herb or MobType.Wood or MobType.Mine
                               or MobType.Object or MobType.NoTarget or MobType.NoTarget2 or MobType.Npc);
}

/// <summary>`MobInfoServer` — the half the client never sees: defences, primaries, detection, rewards.</summary>
public sealed record MobInfoServer(
    int Id, string InxName,
    int Ac, int Tb, int Mr, int Mb,
    int Str, int Dex, int Con, int Int, int Men,
    int MonExp, int DetectCha, int FollowCha,
    int MaxSp, int Rank,
    EnemyDetect DetectType,
    int TurnSpeed, int WalkChase, int RegenInterval)
{
    /// <summary>Whether this mob acquires targets on its own, or only fights back.</summary>
    public bool IsAggressive => DetectType is not (EnemyDetect.Bout or EnemyDetect.NoBrain);

    /// <summary>`TurnSpeed == 0` means the mob turns INSTANTLY — `MobActionTurning::mat_Reserv` returns the
    /// next action without entering the turning state at all. 60 mobs are like this.</summary>
    public bool TurnsInstantly => TurnSpeed == 0;
}

/// <summary>`MobWeapon` — one attack a mob can make.
///
/// <para>A mob may have several rows (5,815 rows across 2,878 mobs), so this is a list per mob rather than
/// a single record. <see cref="Skill"/> is `-` for an ordinary swing.</para>
///
/// <para><see cref="SwingTime"/> and <see cref="HitTime"/> are the two timings the simulation had been
/// inventing: how long a swing takes, and how long after it starts the damage lands.</para></summary>
public sealed record MobWeapon(
    int Id, string InxName, string Skill,
    int AtkSpd, int AtkDly, int SwingTime, int HitTime,
    int MinWc, int MaxWc, int Th,
    int MinMa, int MaxMa, int Mh,
    int Range, HitType HitType, int BlastRate)
{
    /// <summary>Whether this attack is resolved as magic — <c>HitType == HT_MA</c>.
    ///
    /// <para>⚠️ Read the FIELD, do not infer it from MA exceeding WC. Mobs whose normal attack really is
    /// `HT_MA` do tend to have MA far above WC (GoblinMage: WC 19-30, MA 273-415), but the reverse does not
    /// hold — `Pinky` carries MA 72-110 alongside WC 520-792 and is still declared `HT_PY`.</para></summary>
    public bool IsMagical => HitType == HitType.Magical;

    /// <summary>Reach. Melee mobs sit around 10-40; 250+ is a ranged attacker.</summary>
    public bool IsRanged => Range >= 100;
}

/// <summary>Every mob definition, joined across the three tables the server keeps them in.
///
/// <para>`MobInfo.shn` and `MobInfoServer.shn` are one row per mob and join on `ID`; `MobWeapon.shn` is one
/// row per attack and joins on `InxName`, which is also the key `MobRegen` spawn tables use.</para></summary>
public sealed class MobDataBox
{
    public required IReadOnlyDictionary<string, MobInfo> Info { get; init; }
    public required IReadOnlyDictionary<string, MobInfoServer> Server { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<MobWeapon>> Weapons { get; init; }

    public MobInfo? InfoFor(string inxName) => Info.GetValueOrDefault(inxName);

    /// <summary>Whether a spawn-table name refers to something fightable, rather than a gathering node.
    /// An unknown name is treated as NOT fightable — an unrecognised entry is a decode gap, and swinging
    /// at it would hide that.</summary>
    public bool IsFightable(string inxName) => InfoFor(inxName)?.IsFightable ?? false;
    public MobInfoServer? ServerFor(string inxName) => Server.GetValueOrDefault(inxName);

    public IReadOnlyList<MobWeapon> WeaponsFor(string inxName)
        => Weapons.GetValueOrDefault(inxName) ?? [];

    /// <summary>The attack a mob uses <b>against a player</b> — weapon <b>index 0</b>.
    ///
    /// <para>This is the server's rule, not a heuristic. `MobActionAttack::mab_Think` asks the mob for a
    /// weapon index through a virtual, then does
    /// <c>ShineDynamicCast&lt;ShinePlayer&gt;(target)</c> and, <b>if the target is a player, forces the index
    /// to 0</b>. Only non-player targets get the mob's chosen index. The <c>×12</c> that follows is
    /// <c>sizeof(_MobWeaponIndex)</c>, which is how the stride was confirmed.</para>
    ///
    /// <para>An earlier version of this method returned "the first row whose <c>Skill</c> is `-`". That
    /// gives the same answer for every mob in the data — index 0 is the skill-less row in all 2,834 of them
    /// — but it was the right answer for the wrong reason, and it would drift silently if that ever stopped
    /// holding. <c>Index0IsTheSkillessRowForEveryMob</c> pins the coincidence so a change shows up.</para></summary>
    public MobWeapon? AttackAgainstPlayer(string inxName)
    {
        var weapons = WeaponsFor(inxName);
        return weapons.Count > 0 ? weapons[0] : null;
    }

    /// <summary>Load all three tables from a `9Data/Shine` directory.</summary>
    public static MobDataBox Load(string shineDirectory)
    {
        var info = ShnFile.Load(Path.Combine(shineDirectory, "MobInfo.shn"));
        var server = ShnFile.Load(Path.Combine(shineDirectory, "MobInfoServer.shn"));
        var weapon = ShnFile.Load(Path.Combine(shineDirectory, "MobWeapon.shn"));

        static int I(IReadOnlyDictionary<string, object> r, string c) => ShnFile.Int(r, c);
        static string S(IReadOnlyDictionary<string, object> r, string c) => ShnFile.Str(r, c);

        var infos = new Dictionary<string, MobInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in info.Rows)
        {
            var name = S(r, "InxName");
            if (name.Length == 0) continue;
            infos[name] = new MobInfo(
                I(r, "ID"), name, S(r, "Name"),
                I(r, "Level"), I(r, "MaxHP"),
                I(r, "WalkSpeed"), I(r, "RunSpeed"),
                I(r, "IsNPC") != 0, I(r, "Size"),
                (MobType)I(r, "Type"));
        }

        var servers = new Dictionary<string, MobInfoServer>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in server.Rows)
        {
            var name = S(r, "InxName");
            if (name.Length == 0) continue;
            servers[name] = new MobInfoServer(
                I(r, "ID"), name,
                I(r, "AC"), I(r, "TB"), I(r, "MR"), I(r, "MB"),
                I(r, "Str"), I(r, "Dex"), I(r, "Con"), I(r, "Int"), I(r, "Men"),
                I(r, "MonEXP"), I(r, "DetectCha"), I(r, "FollowCha"),
                I(r, "MaxSP"), I(r, "Rank"),
                (EnemyDetect)I(r, "EnemyDetectType"),
                I(r, "TurnSpeed"), I(r, "WalkChase"), I(r, "RegenInterval"));
        }

        var weapons = new Dictionary<string, List<MobWeapon>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in weapon.Rows)
        {
            var name = S(r, "InxName");
            if (name.Length == 0) continue;
            (weapons.TryGetValue(name, out var list) ? list : weapons[name] = []).Add(new MobWeapon(
                I(r, "ID"), name, S(r, "Skill"),
                I(r, "AtkSpd"), I(r, "AtkDly"), I(r, "SwingTime"), I(r, "HitTime"),
                I(r, "MinWC"), I(r, "MaxWC"), I(r, "TH"),
                I(r, "MinMA"), I(r, "MaxMA"), I(r, "MH"),
                I(r, "Range"), (HitType)I(r, "HitType"), I(r, "BlastRate")));
        }

        return new MobDataBox
        {
            Info = infos,
            Server = servers,
            Weapons = weapons.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<MobWeapon>)kv.Value,
                                           StringComparer.OrdinalIgnoreCase),
        };
    }
}
