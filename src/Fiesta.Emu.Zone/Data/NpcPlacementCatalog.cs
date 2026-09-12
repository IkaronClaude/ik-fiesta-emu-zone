namespace Fiesta.Emu.Zone.Data;

/// <summary>One NPC standing somewhere, from `9Data/Shine/World/NPC.txt` (`#Table ShineNPC`).</summary>
/// <param name="MobName">The NPC's mob name -- `MapLinkGate` for a gate.</param>
/// <param name="Map">The map it stands on.</param>
/// <param name="Role">`Role` -- `Gate`, `Smith`, `Healer`, and so on. The thing to select on; a name is
/// not a role.</param>
/// <param name="RoleArg0">The role's argument. For a gate this names the link, e.g. `GateRouVal021`.</param>
public sealed record NpcPlacement(string MobName, string Map, int X, int Y, string Role, string RoleArg0);

/// <summary>Where a map-link GATE leads, from the `LinkTable` in the same file.
///
/// <para>A gate's `RoleArg0` (e.g. `GateRouVal021`) is this row's `argument`, so the two tables together
/// say "this gate, standing here, puts you THERE". That is the whole of cross-map travel in one join, and
/// it is the piece the simulation would need to model a journey that leaves the map.</para></summary>
public sealed record GateLink(string Argument, string ToMap, int ToX, int ToY);

/// <summary>Where the NPCs actually are, read from the server's own `NPC.txt`.
///
/// <para>This is what makes a simulated JOURNEY a real one. A travel destination invented by the harness
/// -- "the furthest spawned mob", say -- is a point with no meaning: nothing about the map says a
/// character would ever go there. A GATE is somewhere a bot genuinely has to reach, it is placed by the
/// server files, and RouVal02 has four of them.</para></summary>
public sealed class NpcPlacementCatalog
{
    /// <summary>`Role` of a map-link gate. The value the file uses, not a guess from the mob name.</summary>
    public const string GateRole = "Gate";

    public required IReadOnlyList<NpcPlacement> Placements { get; init; }

    /// <summary>Every gate destination, by the `argument` a gate's `RoleArg0` names.</summary>
    public required IReadOnlyDictionary<string, GateLink> Links { get; init; }

    /// <summary>Where this gate leads, or null when the file has no link for it.</summary>
    public GateLink? DestinationOf(NpcPlacement gate)
        => gate.RoleArg0.Length > 0 && Links.TryGetValue(gate.RoleArg0, out var l) ? l : null;

    public IEnumerable<NpcPlacement> OnMap(string map)
        => Placements.Where(p => string.Equals(p.Map, map, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<NpcPlacement> GatesOn(string map)
        => OnMap(map).Where(p => string.Equals(p.Role, GateRole, StringComparison.OrdinalIgnoreCase));

    /// <summary>The gate on this map furthest from a point -- the far side of the zone, which is the
    /// journey worth scoring.</summary>
    public NpcPlacement? FurthestGateFrom(string map, int x, int y)
        => GatesOn(map)
            .OrderByDescending(g => (long)(g.X - x) * (g.X - x) + (long)(g.Y - y) * (g.Y - y))
            .FirstOrDefault();

    public static NpcPlacementCatalog Load(string shineDirectory)
    {
        var path = Path.Combine(shineDirectory, "World", "NPC.txt");
        var list = new List<NpcPlacement>();
        var links = new Dictionary<string, GateLink>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return new NpcPlacementCatalog { Placements = list, Links = links };

        // ⚠️ SELECT THE TABLES BY NAME. This file holds TWO -- `ShineNPC` (who stands where) and
        // `LinkTable` (where a gate leads) -- and they share a `Coord-X` column while disagreeing about
        // everything else: LinkTable has `MapServer`, not `Map`. Sniffing for a column picked up both and
        // threw on the first LinkTable row.
        foreach (var table in ShineTable.ParseFile(path))
        {
            if (table.Name.Equals("ShineNPC", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var row in table.Rows)
                {
                    var map = table.Get(row, "Map");
                    if (map.Length == 0) continue;
                    list.Add(new NpcPlacement(
                        MobName: table.Get(row, "MobName"),
                        Map: map,
                        X: table.GetInt(row, "Coord-X"),
                        Y: table.GetInt(row, "Coord-Y"),
                        Role: table.Get(row, "Role"),
                        RoleArg0: table.Get(row, "RoleArg0")));
                }
            }
            else if (table.Name.Equals("LinkTable", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var row in table.Rows)
                {
                    var arg = table.Get(row, "argument");
                    if (arg.Length == 0) continue;
                    links[arg] = new GateLink(
                        Argument: arg,
                        ToMap: table.Get(row, "MapServer"),
                        ToX: table.GetInt(row, "Coord-X"),
                        ToY: table.GetInt(row, "Coord-Y"));
                }
            }
        }

        return new NpcPlacementCatalog { Placements = list, Links = links };
    }
}
