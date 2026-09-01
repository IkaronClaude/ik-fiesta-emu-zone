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
/// <para><b>These are the TABLES, read out of a running zone</b> (0x0DA50BC4 Str, BC8 Int, BCC Dex,
/// BD0 Con, BD4 Men) — all 181 entries of each, not a curve fitted to them. The closed forms in the
/// per-field comments are exact for every entry and are kept as the readable description of each shape,
/// with a test holding the two together; but the table is what the server reads, and a formula that is
/// exact today is a latent bug the day one entry is tweaked.</para>
///
/// <para>This project has been burned once already by a plausible curve fitted to a handful of samples —
/// `FreeStatStr` was "1:1" from two points that happened to agree.</para>
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
    public static int StrWcAbsolute(int points) => StrWcAbsoluteTable[Clamp(points)];

    /// <summary>`FreeStatInt.MAAbsolute` — the magical twin, and the SAME curve. <c>n + n/5</c>.</summary>
    public static int IntMaAbsolute(int points) => IntMaAbsoluteTable[Clamp(points)];

    /// <summary>`FreeStatCon.ACAbsoulte` (the misspelling is the PDB's) — <c>ceil(n/2)</c>.
    ///
    /// <para>This one was a standing worry: the port guessed <c>ceil(n/2)</c> from FOUR samples and
    /// `FUTURE_TESTS.md` listed it as "the same trap, unsprung", because the capture's character had zero
    /// Con and could not exercise it. Read in full, <b>the guess was right</b> — which is worth recording
    /// as much as a correction would have been.</para></summary>
    public static int ConAcAbsolute(int points) => ConAcAbsoluteTable[Clamp(points)];

    /// <summary>`FreeStatMen.MRAbsolute` — the same <c>ceil(n/2)</c> curve as Con's armour.</summary>
    public static int MenMrAbsolute(int points) => MenMrAbsoluteTable[Clamp(points)];

    /// <summary>`FreeStatCon.MaxHP` — a flat <c>5n</c>.</summary>
    public static int ConMaxHp(int points) => ConMaxHpTable[Clamp(points)];

    /// <summary>`FreeStatMen.MaxSP` — a flat <c>5n</c>.</summary>
    public static int MenMaxSp(int points) => MenMaxSpTable[Clamp(points)];

    /// <summary>`FreeStatDex.THRate` — added to <see cref="Combat.DamageCalculator.ToHitRating"/>.
    /// Three bands, each cheaper than the last: <b>+3</b> per point to 33, <b>+2</b> to 67, <b>+1</b>
    /// after.</summary>
    public static int DexThRate(int points) => DexThRateTable[Clamp(points)];

    /// <summary>`FreeStatDex.TBRate` — added to <see cref="Combat.DamageCalculator.ToBlockRating"/>.
    /// <b>+2</b> per point to 50, <b>+1</b> after.</summary>
    public static int DexTbRate(int points) => DexTbRateTable[Clamp(points)];

    /// <summary>`FreeStatCon.BlockRate` — <b>+1</b> per point to 50, then one per TWO points, then FLAT at
    /// 100 from 150. Spending past 150 Con buys no more of it.</summary>
    public static int ConBlockRate(int points) => ConBlockRateTable[Clamp(points)];

    /// <summary>`FreeStatMen.CriRate` — the critical-chance term `roe_FreeStatCriRate` adds, in 1/1000
    /// units.
    ///
    /// <para><b>+2</b> per point to 25, <b>+1</b> to 61, then one per TWO points, then FLAT at 130.</para>
    ///
    /// <para>So 25 points of MEN are worth exactly <b>50</b> — 5% — which is the figure the
    /// operator gave from play before this table was read, and the table agrees to the point.</para></summary>
    public static int MenCriRate(int points) => MenCriRateTable[Clamp(points)];


    // ---- the tables ------------------------------------------------------------------------------------
    //
    // THE DATA, NOT A DERIVATION. An earlier version of this file carried the closed forms in the comments
    // above as the implementation. Every one of them was checked against all 181 entries and every one was
    // exact -- but a formula is still a claim ABOUT the table, and the table is what the server reads.
    // Operator asked for the tables, and they are right: a curve that is exact today is a latent bug the
    // day a server tweaks one entry, and nothing would catch it.
    //
    // The comments keep the closed forms because they are the readable description of the SHAPE, and
    // FreeStatTableTests checks the tables against them -- so if the two ever disagree, that is a finding
    // rather than a silent divergence.
    //
    // Read from a live zone (see the memory note on live container reads): pointer array per stat at
    // 0x0DA50BC4 Str, BC8 Int, BCC Dex, BD0 Con, BD4 Men; table[points] for points 0..180.

    private static readonly int[] StrWcAbsoluteTable =
    [
        0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 12, 13, 14, 15, 16, 18, 19, 20, 21, 22,
        24, 25, 26, 27, 28, 30, 31, 32, 33, 34, 36, 37, 38, 39, 40, 42, 43, 44, 45, 46,
        48, 49, 50, 51, 52, 54, 55, 56, 57, 58, 60, 61, 62, 63, 64, 66, 67, 68, 69, 70,
        72, 73, 74, 75, 76, 78, 79, 80, 81, 82, 84, 85, 86, 87, 88, 90, 91, 92, 93, 94,
        96, 97, 98, 99, 100, 102, 103, 104, 105, 106, 108, 109, 110, 111, 112, 114, 115, 116, 117, 118,
        120, 121, 122, 123, 124, 126, 127, 128, 129, 130, 132, 133, 134, 135, 136, 138, 139, 140, 141, 142,
        144, 145, 146, 147, 148, 150, 151, 152, 153, 154, 156, 157, 158, 159, 160, 162, 163, 164, 165, 166,
        168, 169, 170, 171, 172, 174, 175, 176, 177, 178, 180, 181, 182, 183, 184, 186, 187, 188, 189, 190,
        192, 193, 194, 195, 196, 198, 199, 200, 201, 202, 204, 205, 206, 207, 208, 210, 211, 212, 213, 214,
        216,
    ];

    private static readonly int[] IntMaAbsoluteTable =
    [
        0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 12, 13, 14, 15, 16, 18, 19, 20, 21, 22,
        24, 25, 26, 27, 28, 30, 31, 32, 33, 34, 36, 37, 38, 39, 40, 42, 43, 44, 45, 46,
        48, 49, 50, 51, 52, 54, 55, 56, 57, 58, 60, 61, 62, 63, 64, 66, 67, 68, 69, 70,
        72, 73, 74, 75, 76, 78, 79, 80, 81, 82, 84, 85, 86, 87, 88, 90, 91, 92, 93, 94,
        96, 97, 98, 99, 100, 102, 103, 104, 105, 106, 108, 109, 110, 111, 112, 114, 115, 116, 117, 118,
        120, 121, 122, 123, 124, 126, 127, 128, 129, 130, 132, 133, 134, 135, 136, 138, 139, 140, 141, 142,
        144, 145, 146, 147, 148, 150, 151, 152, 153, 154, 156, 157, 158, 159, 160, 162, 163, 164, 165, 166,
        168, 169, 170, 171, 172, 174, 175, 176, 177, 178, 180, 181, 182, 183, 184, 186, 187, 188, 189, 190,
        192, 193, 194, 195, 196, 198, 199, 200, 201, 202, 204, 205, 206, 207, 208, 210, 211, 212, 213, 214,
        216,
    ];

    private static readonly int[] DexThRateTable =
    [
        0, 3, 6, 9, 12, 15, 18, 21, 24, 27, 30, 33, 36, 39, 42, 45, 48, 51, 54, 57,
        60, 63, 66, 69, 72, 75, 78, 81, 84, 87, 90, 93, 96, 99, 101, 103, 105, 107, 109, 111,
        113, 115, 117, 119, 121, 123, 125, 127, 129, 131, 133, 135, 137, 139, 141, 143, 145, 147, 149, 151,
        153, 155, 157, 159, 161, 163, 165, 167, 168, 169, 170, 171, 172, 173, 174, 175, 176, 177, 178, 179,
        180, 181, 182, 183, 184, 185, 186, 187, 188, 189, 190, 191, 192, 193, 194, 195, 196, 197, 198, 199,
        200, 201, 202, 203, 204, 205, 206, 207, 208, 209, 210, 211, 212, 213, 214, 215, 216, 217, 218, 219,
        220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 235, 236, 237, 238, 239,
        240, 241, 242, 243, 244, 245, 246, 247, 248, 249, 250, 251, 252, 253, 254, 255, 256, 257, 258, 259,
        260, 261, 262, 263, 264, 265, 266, 267, 268, 269, 270, 271, 272, 273, 274, 275, 276, 277, 278, 279,
        280,
    ];

    private static readonly int[] DexTbRateTable =
    [
        0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 34, 36, 38,
        40, 42, 44, 46, 48, 50, 52, 54, 56, 58, 60, 62, 64, 66, 68, 70, 72, 74, 76, 78,
        80, 82, 84, 86, 88, 90, 92, 94, 96, 98, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109,
        110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129,
        130, 131, 132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 142, 143, 144, 145, 146, 147, 148, 149,
        150, 151, 152, 153, 154, 155, 156, 157, 158, 159, 160, 161, 162, 163, 164, 165, 166, 167, 168, 169,
        170, 171, 172, 173, 174, 175, 176, 177, 178, 179, 180, 181, 182, 183, 184, 185, 186, 187, 188, 189,
        190, 191, 192, 193, 194, 195, 196, 197, 198, 199, 200, 201, 202, 203, 204, 205, 206, 207, 208, 209,
        210, 211, 212, 213, 214, 215, 216, 217, 218, 219, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229,
        230,
    ];

    private static readonly int[] ConAcAbsoluteTable =
    [
        0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10,
        10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16, 17, 17, 18, 18, 19, 19, 20,
        20, 21, 21, 22, 22, 23, 23, 24, 24, 25, 25, 26, 26, 27, 27, 28, 28, 29, 29, 30,
        30, 31, 31, 32, 32, 33, 33, 34, 34, 35, 35, 36, 36, 37, 37, 38, 38, 39, 39, 40,
        40, 41, 41, 42, 42, 43, 43, 44, 44, 45, 45, 46, 46, 47, 47, 48, 48, 49, 49, 50,
        50, 51, 51, 52, 52, 53, 53, 54, 54, 55, 55, 56, 56, 57, 57, 58, 58, 59, 59, 60,
        60, 61, 61, 62, 62, 63, 63, 64, 64, 65, 65, 66, 66, 67, 67, 68, 68, 69, 69, 70,
        70, 71, 71, 72, 72, 73, 73, 74, 74, 75, 75, 76, 76, 77, 77, 78, 78, 79, 79, 80,
        80, 81, 81, 82, 82, 83, 83, 84, 84, 85, 85, 86, 86, 87, 87, 88, 88, 89, 89, 90,
        90,
    ];

    private static readonly int[] ConBlockRateTable =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19,
        20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39,
        40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 50, 51, 51, 52, 52, 53, 53, 54, 54,
        55, 55, 56, 56, 57, 57, 58, 58, 59, 59, 60, 60, 61, 61, 62, 62, 63, 63, 64, 64,
        65, 65, 66, 66, 67, 67, 68, 68, 69, 69, 70, 70, 71, 71, 72, 72, 73, 73, 74, 74,
        75, 75, 76, 76, 77, 77, 78, 78, 79, 79, 80, 80, 81, 81, 82, 82, 83, 83, 84, 84,
        85, 85, 86, 86, 87, 87, 88, 88, 89, 89, 90, 90, 91, 91, 92, 92, 93, 93, 94, 94,
        95, 95, 96, 96, 97, 97, 98, 98, 99, 99, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100,
        100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100,
        100,
    ];

    private static readonly int[] ConMaxHpTable =
    [
        0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95,
        100, 105, 110, 115, 120, 125, 130, 135, 140, 145, 150, 155, 160, 165, 170, 175, 180, 185, 190, 195,
        200, 205, 210, 215, 220, 225, 230, 235, 240, 245, 250, 255, 260, 265, 270, 275, 280, 285, 290, 295,
        300, 305, 310, 315, 320, 325, 330, 335, 340, 345, 350, 355, 360, 365, 370, 375, 380, 385, 390, 395,
        400, 405, 410, 415, 420, 425, 430, 435, 440, 445, 450, 455, 460, 465, 470, 475, 480, 485, 490, 495,
        500, 505, 510, 515, 520, 525, 530, 535, 540, 545, 550, 555, 560, 565, 570, 575, 580, 585, 590, 595,
        600, 605, 610, 615, 620, 625, 630, 635, 640, 645, 650, 655, 660, 665, 670, 675, 680, 685, 690, 695,
        700, 705, 710, 715, 720, 725, 730, 735, 740, 745, 750, 755, 760, 765, 770, 775, 780, 785, 790, 795,
        800, 805, 810, 815, 820, 825, 830, 835, 840, 845, 850, 855, 860, 865, 870, 875, 880, 885, 890, 895,
        900,
    ];

    private static readonly int[] MenMrAbsoluteTable =
    [
        0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10,
        10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16, 17, 17, 18, 18, 19, 19, 20,
        20, 21, 21, 22, 22, 23, 23, 24, 24, 25, 25, 26, 26, 27, 27, 28, 28, 29, 29, 30,
        30, 31, 31, 32, 32, 33, 33, 34, 34, 35, 35, 36, 36, 37, 37, 38, 38, 39, 39, 40,
        40, 41, 41, 42, 42, 43, 43, 44, 44, 45, 45, 46, 46, 47, 47, 48, 48, 49, 49, 50,
        50, 51, 51, 52, 52, 53, 53, 54, 54, 55, 55, 56, 56, 57, 57, 58, 58, 59, 59, 60,
        60, 61, 61, 62, 62, 63, 63, 64, 64, 65, 65, 66, 66, 67, 67, 68, 68, 69, 69, 70,
        70, 71, 71, 72, 72, 73, 73, 74, 74, 75, 75, 76, 76, 77, 77, 78, 78, 79, 79, 80,
        80, 81, 81, 82, 82, 83, 83, 84, 84, 85, 85, 86, 86, 87, 87, 88, 88, 89, 89, 90,
        90,
    ];

    private static readonly int[] MenCriRateTable =
    [
        0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 34, 36, 38,
        40, 42, 44, 46, 48, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64,
        65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84,
        85, 86, 86, 87, 87, 88, 88, 89, 89, 90, 90, 91, 91, 92, 92, 93, 93, 94, 94, 95,
        95, 96, 96, 97, 97, 98, 98, 99, 99, 100, 100, 101, 101, 102, 102, 103, 103, 104, 104, 105,
        105, 106, 106, 107, 107, 108, 108, 109, 109, 110, 110, 111, 111, 112, 112, 113, 113, 114, 114, 115,
        115, 116, 116, 117, 117, 118, 118, 119, 119, 120, 120, 121, 121, 122, 122, 123, 123, 124, 124, 125,
        125, 126, 126, 127, 127, 128, 128, 129, 129, 130, 130, 130, 130, 130, 130, 130, 130, 130, 130, 130,
        130, 130, 130, 130, 130, 130, 130, 130, 130, 130, 130, 130, 130, 130, 130, 130, 130, 130, 130, 130,
        130,
    ];

    private static readonly int[] MenMaxSpTable =
    [
        0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95,
        100, 105, 110, 115, 120, 125, 130, 135, 140, 145, 150, 155, 160, 165, 170, 175, 180, 185, 190, 195,
        200, 205, 210, 215, 220, 225, 230, 235, 240, 245, 250, 255, 260, 265, 270, 275, 280, 285, 290, 295,
        300, 305, 310, 315, 320, 325, 330, 335, 340, 345, 350, 355, 360, 365, 370, 375, 380, 385, 390, 395,
        400, 405, 410, 415, 420, 425, 430, 435, 440, 445, 450, 455, 460, 465, 470, 475, 480, 485, 490, 495,
        500, 505, 510, 515, 520, 525, 530, 535, 540, 545, 550, 555, 560, 565, 570, 575, 580, 585, 590, 595,
        600, 605, 610, 615, 620, 625, 630, 635, 640, 645, 650, 655, 660, 665, 670, 675, 680, 685, 690, 695,
        700, 705, 710, 715, 720, 725, 730, 735, 740, 745, 750, 755, 760, 765, 770, 775, 780, 785, 790, 795,
        800, 805, 810, 815, 820, 825, 830, 835, 840, 845, 850, 855, 860, 865, 870, 875, 880, 885, 890, 895,
        900,
    ];
}
