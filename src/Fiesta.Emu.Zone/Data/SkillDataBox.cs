namespace Fiesta.Emu.Zone.Data;

/// <summary>`ActiveSkillInfo` — the client-visible half of an active skill.
///
/// <para>Only the fields something here reads. The table has 96 columns and adding them speculatively
/// would put ninety-odd more values into the "decoded but unconnected" list, which is the category this
/// project is trying to shrink.</para></summary>
public sealed record ActiveSkillInfo(int Id, string InxName, string Name, int CastTimeMs, int DelayTimeMs);

/// <summary>`SkillDataBox` — active skills, keyed the way `MobWeapon` refers to them.
///
/// <para>The server keys its box by numeric skill id (`_MobWeaponIndex.skill`, a `unsigned short`), and
/// resolves `MobWeapon.Skill` to that id when it builds the index. Here the string IS the key, because the
/// join is exact: <b>every one of the 2,974 mob weapon rows that names a skill matches an `ActiveSkill`
/// row</b>, with nothing unmatched. Going through a numeric id would add a step and no information.</para></summary>
public sealed class SkillDataBox
{
    public required IReadOnlyDictionary<string, ActiveSkillInfo> ByName { get; init; }

    /// <summary>An empty box — for a caller with no skill table, where every cast time is zero.
    ///
    /// <para>That is not a fallback that hides a missing file: `MobWeapon.Skill` is `-` for every mob's
    /// weapon row 0, so zero is the RIGHT answer for a normal attack whether the table is loaded or not.
    /// See <see cref="CastTimeMs"/>.</para></summary>
    public static SkillDataBox Empty { get; } = new()
    {
        ByName = new Dictionary<string, ActiveSkillInfo>(StringComparer.OrdinalIgnoreCase),
    };

    public ActiveSkillInfo? Find(string skillName) => ByName.GetValueOrDefault(skillName);

    /// <summary>`ShineMob::sm_GetWeaponCastTime` (0x004B88E0) — the cast time of the skill on a weapon row,
    /// in milliseconds, or 0 when the row names no skill.
    ///
    /// <para>The original looks the row's skill id up in `SkillDataBox`, and <b>returns 0 when the lookup
    /// misses</b> — `test eax,eax; jne; ret` leaves the null pointer in the return register. So an absent
    /// skill is a real zero rather than an error, and this mirrors that.</para>
    ///
    /// <para>⚠️ `-` is how the table spells "no skill", and it is the value on <b>weapon row 0 of every
    /// mob</b> — the row `mab_Think` forces when the target is a player. A mob attacking a character
    /// therefore always has a cast time of zero.</para></summary>
    public int CastTimeMs(MobWeapon weapon)
        => weapon.Skill is "-" or "" ? 0 : Find(weapon.Skill)?.CastTimeMs ?? 0;

    public static SkillDataBox Load(string shineDirectory)
    {
        var rows = ShnFile.Load(Path.Combine(shineDirectory, "ActiveSkill.shn"));
        var byName = new Dictionary<string, ActiveSkillInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows.Rows)
        {
            var inx = ShnFile.Str(r, "InxName");
            if (inx.Length == 0) continue;
            byName[inx] = new ActiveSkillInfo(
                ShnFile.Int(r, "ID"), inx, ShnFile.Str(r, "Name"),
                ShnFile.Int(r, "CastTime"), ShnFile.Int(r, "DlyTime"));
        }
        return new SkillDataBox { ByName = byName };
    }
}
