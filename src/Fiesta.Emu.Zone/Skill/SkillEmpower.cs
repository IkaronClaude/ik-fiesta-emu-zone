namespace Fiesta.Emu.Zone.Skill;

/// <summary>`SKILL_EMPOWER` — the per-cast empower allocation, two bytes holding FOUR 4-bit fields.
///
/// <para>The PDB's layout, all four at +0x00 as bitfields:</para>
/// <code>
/// damage : 4     sp : 4     keeptime : 4     cooltime : 4
/// </code>
///
/// <para>So a cast spends points across four axes, each 0..15, and the client asks for the split with
/// `PROTO_NC_SKILL_EMPOWALLOC_REQ` (`csl_EmpowerAlloc`, 0x00448570). It rides on the cast in
/// <c>EngageArgument.empower</c> (+0x0C), which is why the damage engine can see it at all.</para>
///
/// <para><b>Zero is a real allocation</b> — "no points spent on damage" — and `roe_AttackPower` tests it
/// explicitly before doing the lookup, so it is not a marker for "unset".</para></summary>
/// <param name="Raw">The 16-bit word as it appears in `EngageArgument.empower`.</param>
public readonly record struct SkillEmpower(ushort Raw)
{
    /// <summary>Bits 0-3. The only field the damage path reads.</summary>
    public int Damage => Raw & 0xF;

    /// <summary>Bits 4-7.</summary>
    public int Sp => (Raw >> 4) & 0xF;

    /// <summary>Bits 8-11.</summary>
    public int KeepTime => (Raw >> 8) & 0xF;

    /// <summary>Bits 12-15.</summary>
    public int CoolTime => (Raw >> 12) & 0xF;

    /// <summary>Nothing empowered — what a normal swing carries.</summary>
    public static SkillEmpower None => new(0);

    public static SkillEmpower From(int damage, int sp = 0, int keepTime = 0, int coolTime = 0)
        => new((ushort)((damage & 0xF) | ((sp & 0xF) << 4) | ((keepTime & 0xF) << 8) | ((coolTime & 0xF) << 12)));
}

/// <summary>The per-skill empower table — `ActiveSkillInfo`'s `nT0`..`nT3`.
///
/// <para>`roe_AttackPower` (NormalPY at +0x658, PhisycalSkill and MagicalSkill at +0x178) does exactly
/// this:</para>
///
/// <code>
/// level = (u16)arg-&gt;empower &amp; 0xF;               // SKILL_EMPOWER.damage
/// if (level == 0) term = 0;
/// else term = *(u32*)(arg-&gt;sklinfo-&gt;sdi_Activ + level*4 + 0x1BB);
/// </code>
///
/// <para><c>+0x1BB + level*4</c> lands on <c>nT0[level-1]</c> at level 1, and `nT0`..`nT3` are four
/// contiguous <c>unsigned long[5]</c> starting at +0x1BF — so the four declared arrays are indexed as ONE
/// flat run of twenty, and the empower damage level walks straight across the boundaries. Reading them as
/// four separate five-entry tables would give the wrong value for every level above 5.</para>
///
/// <para>The value is loaded UNSIGNED (the original adds 2^32 when the signed load came out negative), so
/// a table entry above 2^31 is a large positive number, not a negative one.</para>
///
/// <para>⚠️ <b>Where this term lands in the attack-power chain is NOT yet read.</b> The lookup above is
/// exact; what `roe_AttackPower` then does with it — added before or after which rate, inside or outside
/// which truncation — is the next thing to read, and this class deliberately stops at the lookup rather
/// than guessing the arithmetic.</para></summary>
public sealed class SkillEmpowerTable(IReadOnlyList<uint> flat)
{
    /// <summary>`nT0`..`nT3` as the engine indexes them: one flat run of twenty.</summary>
    public const int Entries = 20;

    /// <summary>The highest damage level the four-bit field can hold.</summary>
    public const int MaxLevel = 15;

    private readonly IReadOnlyList<uint> _flat = flat;

    /// <summary>Build from the four declared arrays, in order. They are contiguous in memory and the
    /// engine does not respect the boundaries between them.</summary>
    public static SkillEmpowerTable FromArrays(IReadOnlyList<uint> nT0, IReadOnlyList<uint> nT1,
                                               IReadOnlyList<uint> nT2, IReadOnlyList<uint> nT3)
        => new([.. nT0, .. nT1, .. nT2, .. nT3]);

    /// <summary>The damage term for an empower allocation, or 0 when no points were spent on damage.</summary>
    public uint DamageTerm(SkillEmpower empower)
    {
        var level = empower.Damage;
        if (level == 0) return 0;
        var index = level - 1;
        return index < _flat.Count ? _flat[index] : 0;
    }
}
