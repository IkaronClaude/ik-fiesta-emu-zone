namespace Fiesta.Emu.Zone.Data;

/// <summary>Where a mob or NPC stands on a map — one row of `MobCoordinate.shn`.</summary>
/// <param name="MobId">`Mob_ID`, the id the quest tables and the wire use.</param>
/// <param name="Map">`MapName`, e.g. `RouVal02`.</param>
/// <param name="CenterX">Centre of the placement area.</param>
/// <param name="CenterY">Centre of the placement area.</param>
/// <param name="Width">Extent of the area; a roaming mob wanders inside it.</param>
/// <param name="Height">Extent of the area.</param>
public sealed record MobPlacement(int MobId, string Map, int CenterX, int CenterY, int Width, int Height);

/// <summary>⭐ `MobCoordinate.shn` — WHERE NPCs ARE, which is not in `MobRegen` at all.
///
/// <para>⚠️ This was the missing half of the quest surface, and the mistake was assuming NPCs came from
/// the same place monsters do. `MobRegen` carries the spawn groups a map fights: on Burning Hill its
/// eleven non-combat entries are <b>Herb, Wood and Mine nodes</b> and not one NPC. Quest givers are
/// placed here instead, keyed by mob id and map.</para>
///
/// <para>That is why `npcCoord` and `npcSeedCount` were stubs while everything else got backed, and why
/// `level_quest.lua` spends every simulated run in its "no active quests KNOWN yet" crutch: it has no NPC
/// to take a quest from, so its whole phase machine idles.</para>
///
/// <para>The live bot reads exactly this table for `npcCoord` — <c>ClientData.MobCoordinate(npcId, map)</c>
/// returning <c>{ map, x, y }</c> from `CenterX`/`CenterY`.</para></summary>
public sealed class MobCoordinateCatalog
{
    public required IReadOnlyList<MobPlacement> Placements { get; init; }

    private Dictionary<(int MobId, string Map), MobPlacement>? _byMobAndMap;
    private Dictionary<string, List<MobPlacement>>? _byMap;

    /// <summary>Where this mob id stands on this map, or null when it is not placed there.</summary>
    public MobPlacement? For(int mobId, string map)
    {
        _byMobAndMap ??= BuildIndex();
        return _byMobAndMap.GetValueOrDefault((mobId, map));
    }

    /// <summary>Everything placed on a map.</summary>
    public IReadOnlyList<MobPlacement> OnMap(string map)
    {
        _byMap ??= Placements
            .GroupBy(p => p.Map, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        return _byMap.GetValueOrDefault(map) ?? [];
    }

    private Dictionary<(int, string), MobPlacement> BuildIndex()
    {
        // ⚠️ First placement wins. A mob id can appear more than once on a map — several copies of the
        // same guard — and `ToDictionary` would throw on that, which is how this class would have
        // announced itself as broken on a perfectly ordinary table.
        var index = new Dictionary<(int, string), MobPlacement>();
        foreach (var p in Placements) index.TryAdd((p.MobId, p.Map), p);
        return index;
    }

    public static MobCoordinateCatalog Load(string ressystemDirectory)
    {
        var table = ShnFile.Load(Path.Combine(ressystemDirectory, "MobCoordinate.shn"));
        return new MobCoordinateCatalog
        {
            Placements =
            [
                .. table.Rows.Select(r => new MobPlacement(
                    ShnFile.Int(r, "Mob_ID"),
                    ShnFile.Str(r, "MapName"),
                    ShnFile.Int(r, "CenterX"),
                    ShnFile.Int(r, "CenterY"),
                    ShnFile.Int(r, "Width"),
                    ShnFile.Int(r, "Height")))
            ],
        };
    }
}
