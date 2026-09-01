namespace Fiesta.Emu.Zone.Parameter;

/// <summary>`ShineCommonParameter::FreeStat*` — what SPENT stat points are actually worth.
///
/// <para>Five tables of 181 records, one per allocated point count, reached through a pointer array per
/// stat: <c>table = *(Record***)0x0DA50BC?; return table[points];</c> — `so_ply_FreeStat*@ShinePlayer`
/// (0x0055A350..0x0055A450). The record layouts are the PDB's:</para>
///
/// <code>
/// FreeStatStr  4B  { Stat, WCAbsolute, checksum }
/// FreeStatInt  4B  { Stat, MAAbsolute, checksum }
/// FreeStatDex  6B  { Stat, THRate, TBRate, checksum }
/// FreeStatCon  8B  { Stat, ACAbsoulte, BlockRate, MaxHP, checksum }
/// FreeStatMen  8B  { Stat, MRAbsolute, CriRate, MaxSP, checksum }
/// </code>
///
/// <para><b>Every formula below was read out of a running zone and checked against all 181 entries</b>
/// (0x0DA50BC4 Str, BC8 Int, BCC Dex, BD0 Con, BD4 Men). None is fitted: each is exact everywhere or it is
/// not here. That matters because this project has been burned once already by a plausible curve fitted to
/// a handful of samples — `FreeStatStr` was "1:1" from two points that happened to agree.</para>
///
/// <para>⚠️ <b>`Stat` is not what callers read.</b> The first byte echoes the point count and every caller
/// reads a later field, which is the trap the original `FreeStatStr` reading fell into.</para></summary>
public static class FreeStatTables
{
    /// <summary>The highest allocation the tables hold. Index 0 is a real entry — no points spent, every
    /// field 0 — so the range is 0..180 inclusive.</summary>
    public const int MaxPoints = 180;

    private static int Clamp(int points) => points < 0 ? 0 : points > MaxPoints ? MaxPoints : points;

    /// <summary>`FreeStatStr.WCAbsolute` — added to both weapon bounds by the displayed-damage builder and
    /// by `roe_Damage`'s physical override. <c>n + n/5</c>, exact for all 181.</summary>
    public static int StrWcAbsolute(int points) { var n = Clamp(points); return n + n / 5; }

    /// <summary>`FreeStatInt.MAAbsolute` — the magical twin, and the SAME curve. <c>n + n/5</c>.</summary>
    public static int IntMaAbsolute(int points) { var n = Clamp(points); return n + n / 5; }

    /// <summary>`FreeStatCon.ACAbsoulte` (the misspelling is the PDB's) — <c>ceil(n/2)</c>.
    ///
    /// <para>This one was a standing worry: the port guessed <c>ceil(n/2)</c> from FOUR samples and
    /// `FUTURE_TESTS.md` listed it as "the same trap, unsprung", because the capture's character had zero
    /// Con and could not exercise it. Read in full, <b>the guess was right</b> — which is worth recording
    /// as much as a correction would have been.</para></summary>
    public static int ConAcAbsolute(int points) { var n = Clamp(points); return (n + 1) / 2; }

    /// <summary>`FreeStatMen.MRAbsolute` — the same <c>ceil(n/2)</c> curve as Con's armour.</summary>
    public static int MenMrAbsolute(int points) { var n = Clamp(points); return (n + 1) / 2; }

    /// <summary>`FreeStatCon.MaxHP` — a flat <c>5n</c>.</summary>
    public static int ConMaxHp(int points) => 5 * Clamp(points);

    /// <summary>`FreeStatMen.MaxSP` — a flat <c>5n</c>.</summary>
    public static int MenMaxSp(int points) => 5 * Clamp(points);

    /// <summary>`FreeStatDex.THRate` — added to <see cref="Combat.DamageCalculator.ToHitRating"/>.
    /// Three bands, each cheaper than the last: <b>+3</b> per point to 33, <b>+2</b> to 67, <b>+1</b>
    /// after.</summary>
    public static int DexThRate(int points)
    {
        var n = Clamp(points);
        if (n <= 33) return 3 * n;
        if (n <= 67) return 99 + 2 * (n - 33);
        return 167 + (n - 67);
    }

    /// <summary>`FreeStatDex.TBRate` — added to <see cref="Combat.DamageCalculator.ToBlockRating"/>.
    /// <b>+2</b> per point to 50, <b>+1</b> after.</summary>
    public static int DexTbRate(int points)
    {
        var n = Clamp(points);
        return n <= 50 ? 2 * n : 100 + (n - 50);
    }

    /// <summary>`FreeStatCon.BlockRate` — <b>+1</b> per point to 50, then one per TWO points, then FLAT at
    /// 100 from 150. Spending past 150 Con buys no more of it.</summary>
    public static int ConBlockRate(int points)
    {
        var n = Clamp(points);
        if (n <= 50) return n;
        return n <= 150 ? 50 + (n - 50) / 2 : 100;
    }

    /// <summary>`FreeStatMen.CriRate` — the critical-chance term `roe_FreeStatCriRate` adds, in permille.
    ///
    /// <para><b>+2</b> per point to 25, <b>+1</b> to 61, then one per TWO points, then FLAT at 130.</para>
    ///
    /// <para>So 25 points of MEN are worth exactly <b>+50 permille</b> — 5% — which is the figure the
    /// operator gave from play before this table was read, and the table agrees to the point.</para></summary>
    public static int MenCriRate(int points)
    {
        var n = Clamp(points);
        if (n <= 25) return 2 * n;
        if (n <= 61) return 50 + (n - 25);
        return Math.Min(86 + (n - 61) / 2, 130);
    }
}
