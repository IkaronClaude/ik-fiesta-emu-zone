namespace Fiesta.Emu.Zone.Parameter;

/// <summary>`Parameter::ChangeByConditionParam` — a stat bonus that scales with a CONDITION rather than
/// being flat.
///
/// <para>In this data the condition is always <b>how much HP the owner is missing, in permille</b>, and the
/// six blocks on <see cref="ParameterContainer"/> are the "as you get hurt, you hit harder" passives:
/// `PassiveHPDownRateWCMin/Max`, `PassiveHPDownRateMAMin/Max`, `PassiveHPDownRateAC` and
/// `PassiveHPDownRateMR`. The PDB names them; nothing was guessed here.</para>
///
/// <para>The lookup is `cbcp_GetValue` (0x004C8FE0), and it is four instructions of substance:</para>
/// <code>
/// int cbcp_GetValue(unsigned key) {
///     if (cbcp_nMaxValueNum == 0) return 0;          // +0x7   nothing configured
///     unsigned i = key / cbcp_nCondition;            // +0x10  bucket width
///     if (i >= cbcp_nMaxValueNum) return 0;          // +0x16  past the last bucket
///     return cbcp_pValue[i];                         // +0x1D
/// }
/// </code>
///
/// <para><b>Both guards return 0, not the nearest bucket.</b> An unconfigured block contributes nothing,
/// which is why this can be added to the formula without changing any existing result: a character with no
/// such passive has <see cref="BucketCount"/> zero and the term vanishes.</para>
///
/// <para>⚠️ The divide is UNSIGNED (`div`, not `idiv`), so a negative key would wrap to an enormous bucket
/// index and fail the range check — returning 0 rather than reading out of bounds. Callers pass a permille
/// that cannot be negative anyway; the note is here so nobody "fixes" it into a signed divide.</para></summary>
/// <param name="Condition">`cbcp_nCondition` — the bucket WIDTH, the divisor applied to the key.</param>
/// <param name="Values">`cbcp_pValue` — one bonus per bucket.</param>
public sealed record ChangeByConditionParam(int Condition, IReadOnlyList<int> Values)
{
    /// <summary>An unconfigured block: contributes 0 for every key, like a container the server never
    /// filled. This is the default on <see cref="ParameterContainer"/>.</summary>
    public static ChangeByConditionParam None { get; } = new(1, []);

    /// <summary>`cbcp_nMaxValueNum` — a BYTE in the original, so at most 255 buckets.</summary>
    public int BucketCount => Values.Count;

    /// <summary>The bonus for a condition value. Zero when nothing is configured or the key is past the
    /// last bucket.</summary>
    public int Value(int key)
    {
        if (BucketCount == 0 || Condition <= 0 || key < 0) return 0;
        var index = (uint)key / (uint)Condition;
        return index >= (uint)BucketCount ? 0 : Values[(int)index];
    }

    /// <summary>`cbcp_GetValue_Index` (0x004C9010) — the same block read by RAW INDEX, with no divide.
    ///
    /// <para>A second, simpler accessor the binary keeps alongside <see cref="Value"/>:</para>
    /// <code>
    /// int cbcp_GetValue_Index(int i) {
    ///     if (cbcp_nMaxValueNum == 0) return 0;   // +0x3
    ///     if (i >= cbcp_nMaxValueNum) return 0;   // +0x12  SIGNED compare
    ///     return cbcp_pValue[i];                  // +0x17
    /// }
    /// </code>
    ///
    /// <para>Its only caller in the damage path is `roe_HitRate@NormalPY`, reading
    /// <see cref="ParameterContainer.PassiveMovingTbPlus"/> at index 0.</para>
    ///
    /// <para>⚠️ The original's range check is a SIGNED <c>jge</c> with no lower bound, so a negative index
    /// would read before the array. This port refuses it instead — no caller passes one, and reproducing
    /// an out-of-bounds read is not fidelity.</para></summary>
    public int ValueAtIndex(int index)
        => index < 0 || index >= BucketCount ? 0 : Values[index];

    /// <summary>The key the damage functions pass: how much HP the owner is MISSING, in permille.
    ///
    /// <para>From `roe_AttackPower+0xA4`: <c>(maxHp - hp) * 1000 / maxHp</c>, an unsigned divide. At full
    /// HP this is 0 and at death 1000, so the buckets run from "untouched" upward — the bonus GROWS as the
    /// owner is hurt, which is what the `HPDown` in the field names means.</para>
    ///
    /// <para>Returns 0 when <paramref name="maxHp"/> is not positive, matching the
    /// <c>if (maxHp &gt; 0)</c> guard at `roe_AttackPower+0x90` that skips the whole block.</para></summary>
    public static int HpMissingPermille(int hp, int maxHp)
        => maxHp <= 0 ? 0 : (int)((uint)Math.Max(0, maxHp - hp) * 1000u / (uint)maxHp);
}
