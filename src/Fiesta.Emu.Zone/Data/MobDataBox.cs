namespace Fiesta.Emu.Zone.Data;

/// <summary>`MobInfo` — the client-visible half of a mob's definition.
///
/// <para>Field names and the order below are the PDB's `MobInfo` struct. <see cref="MaxHp"/> sits at +0x46,
/// which is exactly what `CharClassMob::MaxHP` (0x004496F0) reads: it ignores the stat cluster entirely and
/// returns this number.</para></summary>
public sealed record MobInfo(
    int Id, string InxName, string Name,
    int Level, int MaxHp,
    int WalkSpeed, int RunSpeed,
    bool IsNpc, int Size);

/// <summary>`MobInfoServer` — the half the client never sees: defences, primaries, detection, rewards.</summary>
public sealed record MobInfoServer(
    int Id, string InxName,
    int Ac, int Tb, int Mr, int Mb,
    int Str, int Dex, int Con, int Int, int Men,
    int MonExp, int DetectCha, int FollowCha,
    int MaxSp, int Rank);

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
    int Range);

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
    public MobInfoServer? ServerFor(string inxName) => Server.GetValueOrDefault(inxName);

    public IReadOnlyList<MobWeapon> WeaponsFor(string inxName)
        => Weapons.GetValueOrDefault(inxName) ?? [];

    /// <summary>The mob's ordinary attack — the first row whose <c>Skill</c> is not set.
    ///
    /// <para>`-` is the file's way of writing "no skill", so an ordinary swing is a row with that marker.
    /// Returns null when a mob has only skill attacks, which is a real case and not an error.</para></summary>
    public MobWeapon? NormalAttackOf(string inxName)
        => WeaponsFor(inxName).FirstOrDefault(w => w.Skill is "-" or "");

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
                I(r, "IsNPC") != 0, I(r, "Size"));
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
                I(r, "MaxSP"), I(r, "Rank"));
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
                I(r, "Range")));
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
