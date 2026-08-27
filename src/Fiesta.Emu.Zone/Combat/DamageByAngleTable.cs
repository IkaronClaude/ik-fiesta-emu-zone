namespace Fiesta.Emu.Zone.Combat;

/// <summary>`DamageByAngle::DamageTable` — how much harder a hit lands from the side or behind.
///
/// <para>The source table (`9Data/Shine/World/DamageByAngle.txt`) is six ANCHOR points in degrees, and the
/// server expands them into a dense <b>91-entry array indexed in DIRECTION UNITS</b> (0–90 units = 0–180°).
/// Both `DamageByAngle_Chr` and `DamageByAngle_Mob` are present and, in this data, identical:</para>
///
/// <list type="table">
///   <item><term>0°</term><description>1000 — head-on</description></item>
///   <item><term>45°</term><description>1040</description></item>
///   <item><term>90°</term><description>1100 — from the side</description></item>
///   <item><term>135°</term><description>1120</description></item>
///   <item><term>170°</term><description>1140</description></item>
///   <item><term>180°</term><description>1200 — from directly behind</description></item>
/// </list>
///
/// <para><b>The whole range is 1000–1200.</b> That is worth stating because it bounds how much of an
/// observed damage spread a backstab can explain: at most 20%, so a wider spread than that in a capture is
/// something else and must not be written off as "the angle".</para>
///
/// <para>Ported from `dt_Load` (0x0045CA10) and `operator[]` (0x0045C9A0).</para></summary>
public sealed class DamageByAngleTable
{
    /// <summary>0–90 direction units, i.e. 0–180° in the game's 2° quantum.</summary>
    public const int MaxIndex = 90;

    /// <summary>What `operator[]` returns for an index it cannot fold into range — and `dt_Load`'s value
    /// for a slot no anchor reaches.</summary>
    public const int NoAdjustment = 1000;

    private readonly int[] _rates;

    private DamageByAngleTable(int[] rates) => _rates = rates;

    /// <summary>The dense expanded table, index 0 (head-on) to 90 (behind).</summary>
    public IReadOnlyList<int> Rates => _rates;

    /// <summary>`DamageByAngle::DamageTable::operator[]` — the rate for a direction-unit difference.
    ///
    /// <para>Takes the absolute value, folds anything past 90 back into range, and returns 1000 if it still
    /// cannot (which the original also logs as an error). Feed it
    /// <see cref="DamageCalculator.AngleDamageIndex"/>'s output, or a raw difference — the folding here is
    /// the server's own and handles either.</para></summary>
    public int this[int directionUnitDelta]
    {
        get
        {
            var i = Math.Abs(directionUnitDelta);
            if (i > MaxIndex)
            {
                // `i = i - 180 - ((i - 91) / 180) * 180`, the magic-divide sequence at operator[]+0x12.
                i = i - 180 - (i - 91) / 180 * 180;
                i = Math.Abs(i);
            }
            return i is >= 0 and <= MaxIndex ? _rates[i] : NoAdjustment;
        }
    }

    /// <summary>`sr_degree2sr` (0x00579180) — degrees to direction units.
    ///
    /// <para>It normalises into [0, 360) and then computes <c>d * 180 / 360</c>, so it is a truncating halve:
    /// the six anchors land on indices 0, 22, 45, 67, 85 and 90. <b>45° becomes 22, not 22.5</b>, and that
    /// truncation moves where the ramp starts.</para></summary>
    public static int DegreesToUnits(int degrees)
    {
        var d = degrees % 360;
        if (d < 0) d += 360;
        return d * 180 / 360;
    }

    /// <summary>Expand anchors into the dense table exactly as `dt_Load+0x233` does.
    ///
    /// <para>⚠️ <b>This is not a linear interpolation, and writing it as one gives different numbers.</b>
    /// The original walks slot by slot and recomputes the step from the CURRENT value every time:</para>
    /// <code>
    /// for (i = 1; i &lt; 90; i++) {
    ///     if (table[i] != 0) continue;                       // +0x240: an anchor is left alone
    ///     j = i + 1; while (j &lt;= 90 &amp;&amp; table[j] == 0) j++;   // next anchor
    ///     table[i] = table[i-1] + (table[j] - table[i-1]) / (j - i + 1);
    /// }
    /// </code>
    /// Each division truncates and the result feeds the next step, so the ramp is a repeated-averaging
    /// curve rather than a straight line — it hugs the lower anchor and accelerates.</para></summary>
    public static DamageByAngleTable FromAnchors(IEnumerable<(int Degrees, int Rate)> anchors)
    {
        var t = new int[MaxIndex + 1];
        foreach (var (deg, rate) in anchors)
        {
            var idx = DegreesToUnits(deg);
            if (idx is >= 0 and <= MaxIndex) t[idx] = rate;
        }

        // `dt_Load+0x202` requires both ends: it calls ShineExit if index 0 or index 90 is empty, and the
        // source file says so in as many words ("0, 180 must always be present").
        if (t[0] == 0 || t[MaxIndex] == 0)
            throw new InvalidDataException("DamageByAngle needs anchors at both 0 and 180 degrees");

        for (var i = 1; i < MaxIndex; i++)
        {
            if (t[i] != 0) continue;
            var j = i + 1;
            while (j <= MaxIndex && t[j] == 0) j++;
            if (j > MaxIndex) break;
            t[i] = t[i - 1] + (t[j] - t[i - 1]) / (j - i + 1);
        }
        return new DamageByAngleTable(t);
    }

    /// <summary>Load one of the two tables out of `DamageByAngle.txt`.
    ///
    /// <para>The file holds BOTH — `DamageByAngle_Chr` and `DamageByAngle_Mob` — one after the other, so the
    /// records have to be attributed to the `#Table` directive above them. Reading the file as a flat list
    /// of `#Record` lines silently concatenates the two.</para></summary>
    public static DamageByAngleTable Load(string worldDirectory, string table = "DamageByAngle_Chr")
    {
        var path = Path.Combine(worldDirectory, "DamageByAngle.txt");
        var anchors = new List<(int, int)>();
        var inTable = false;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Replace("\r", "").Trim();
            if (line.StartsWith("#Table", StringComparison.Ordinal))
            {
                inTable = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries)
                              .Skip(1).FirstOrDefault() == table;
            }
            else if (inTable && line.StartsWith("#Record", StringComparison.Ordinal))
            {
                var f = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 3 && int.TryParse(f[1], out var deg) && int.TryParse(f[2], out var rate))
                    anchors.Add((deg, rate));
            }
        }

        if (anchors.Count == 0)
            throw new InvalidDataException($"no records for {table} in {path}");
        return FromAnchors(anchors);
    }
}
