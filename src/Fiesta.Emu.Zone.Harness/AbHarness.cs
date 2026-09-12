using System.Text;

namespace Fiesta.Emu.Zone.Lua;

/// <summary>One combat script under test.</summary>
/// <param name="Name">What to call it in the scorecard.</param>
/// <param name="Source">The Lua. Both variants get the identical world, so any difference is the script.</param>
public sealed record ScriptVariant(string Name, string Source)
{
    public static ScriptVariant FromFile(string name, string path) => new(name, File.ReadAllText(path));
}

/// <summary>One cell of the experiment: who, where, at what level.</summary>
public sealed record AbCell(string ClassName, int Level, bool Dungeon)
{
    public override string ToString()
        => $"{ClassName} L{Level} {(Dungeon ? "dungeon" : "field")}";
}

/// <summary>What one variant scored in one cell on one seed.</summary>
public sealed record AbRun(ScriptVariant Variant, AbCell Cell, uint Seed,
                           ScenarioResult Result, CombatMetrics Metrics);

/// <summary>A combat-script A/B test: the SAME world, the SAME seed, two scripts.
///
/// <para>The point is that a single number cannot rank two combat scripts. Kills reward a script that
/// stands in a pack and dies; survival rewards one that never fights. So a run is scored on the axes the
/// operator named - kills per minute alive, deaths, ability DEAD TIME, kites that end somewhere more
/// crowded than they started, and resource handling - and the scorecard prints all of them side by side
/// rather than collapsing them into one figure that hides the trade.</para>
///
/// <para><b>Determinism is the whole experiment.</b> Same seed, same spawn seed, same map, same
/// best-in-slot loadout, same tick budget. If a variant wins on one seed and loses on the next, that is
/// the answer: the difference is noise, not the script. Run several seeds and read the spread, never a
/// single run - this project has twice been fooled by a one-run margin.</para></summary>
public static class AbHarness
{
    /// <summary>Seeds to run each cell on. Three is enough to catch a result that is really noise and
    /// cheap enough to run a whole matrix.</summary>
    public static readonly uint[] DefaultSeeds = [42, 1337, 9001];

    /// <summary>Run one variant in one cell on one seed.</summary>
    public static AbRun? Run(string shine, string ressystem, ScriptVariant variant, AbCell cell, uint seed,
                             int ticks = ScenarioRunner.DefaultTicks)
    {
        var metrics = new CombatMetrics();
        var result = ScenarioRunner.Run(shine, ressystem, variant.Source,
                                        cell.ClassName, cell.Level, cell.Dungeon,
                                        ticks: ticks, seed: seed, metrics: metrics);
        return result is null ? null : new AbRun(variant, cell, seed, result, metrics);
    }

    /// <summary>Run every variant over every cell on every seed. Yields as it goes so a long sweep can be
    /// watched rather than waited on.</summary>
    public static IEnumerable<AbRun> RunMatrix(
        string shine, string ressystem,
        IEnumerable<ScriptVariant> variants, IEnumerable<AbCell> cells,
        IEnumerable<uint>? seeds = null, int ticks = ScenarioRunner.DefaultTicks)
    {
        seeds ??= DefaultSeeds;
        var cellList = cells.ToList();
        var seedList = seeds.ToList();
        foreach (var cell in cellList)
            foreach (var seed in seedList)
                foreach (var variant in variants)          // variants innermost: adjacent in the output
                {
                    var run = Run(shine, ressystem, variant, cell, seed, ticks);
                    if (run is not null) yield return run;
                }
    }

    /// <summary>The per-axis average for one variant over a set of runs.</summary>
    public sealed record Score(string Variant, int Runs, double Kills, double AlivePercent,
                               double KillsPerMinute, double ExpPerMinute,
                               int Deaths, double DeadTimePercent, double BadKitePercent,
                               double KitePercent, double LowWaterHp, double SpCapped, double SpStarved,
                               double HpStones, int Errors,
                               double DpsOut, double DpsIn, double DamageRatio,
                               int NearDeathRuns, double NearDeathPercent)
    {
        /// <summary>Named so the SAFETY table reads the same way as the field it prints.</summary>
        public double BadKite() => BadKitePercent;

        public static Score Of(string variant, IReadOnlyList<AbRun> runs)
        {
            if (runs.Count == 0)
                return new Score(variant, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            return new Score(variant, runs.Count,
                runs.Average(r => r.Result.Kills),
                runs.Average(r => r.Result.AlivePercent),
                runs.Average(r => r.Result.KillsPerMinute),
                runs.Average(r => r.Result.ExpPerMinute),
                runs.Count(r => r.Result.Died),
                runs.Average(r => r.Metrics.DeadTimePercent),
                runs.Average(r => r.Metrics.BadKitePercent),
                runs.Average(r => r.Metrics.KitePercent),
                runs.Average(r => r.Metrics.LowWaterHpPercent),
                runs.Average(r => r.Metrics.SpCappedPercent),
                runs.Average(r => r.Metrics.SpStarvedPercent),
                runs.Average(r => r.Metrics.HpStonesUsed),
                runs.Sum(r => r.Result.Errors),
                runs.Average(r => r.Metrics.DpsOut),
                runs.Average(r => r.Metrics.DpsIn),
                runs.Select(r => r.Metrics.DamageRatio).OfType<double>().DefaultIfEmpty().Average(),
                runs.Count(r => r.Metrics.EverNearDeath),
                runs.Average(r => r.Metrics.NearDeathPercentOfRun));
        }
    }

    /// <summary>The scorecard: every axis for every variant, then the per-cell detail.
    ///
    /// <para>No overall "winner" is declared, deliberately. Which axis matters is the operator's call -
    /// a script that trades 10% of its kill rate for zero deaths may be exactly what is wanted, and a
    /// harness that collapsed that into one score would hide the trade it was built to show.</para></summary>
    public static string Scorecard(IEnumerable<AbRun> runs)
    {
        var all = runs.ToList();
        if (all.Count == 0) return "no runs";

        var sb = new StringBuilder();
        var variants = all.Select(r => r.Variant.Name).Distinct().ToList();

        sb.AppendLine("OVERALL  (mean over every cell and seed; ^ = higher is better, v = lower is better)");
        sb.AppendLine("  OUTPUT   variant          runs  kills^  alive%^  kills/min^  exp/min^   dpsOut^   dpsIn v  dmgRatio^  dead%v");
        foreach (var v in variants)
        {
            var s = Score.Of(v, all.Where(r => r.Variant.Name == v).ToList());
            sb.AppendLine($"           {s.Variant,-15} {s.Runs,4}  {s.Kills,6:F1}  {s.AlivePercent,7:F1}  "
                          + $"{s.KillsPerMinute,10:F2}  {s.ExpPerMinute,8:F0}  "
                          + $"{s.DpsOut,8:F1}  {s.DpsIn,7:F1}  {s.DamageRatio,9:F2}  {s.DeadTimePercent,6:F1}");
        }
        sb.AppendLine();
        sb.AppendLine("  SAFETY   variant          deaths v  nearDeathRuns v  nearDeath%v  lowHp^  badKite%v  kite%  bursts  meanS  maxS");
        foreach (var v in variants)
        {
            var rs2 = all.Where(r => r.Variant.Name == v).ToList();
            var s = Score.Of(v, rs2);
            sb.AppendLine($"           {s.Variant,-15} {s.Deaths,8}  {s.NearDeathRuns,15}  "
                          + $"{s.NearDeathPercent,11:F1}  {s.LowWaterHp,6:F0}  {s.BadKite(),9:F1}  {s.KitePercent,5:F1}  "
                          + $"{rs2.Average(r => r.Metrics.KiteBursts),6:F0}  "
                          + $"{rs2.Average(r => r.Metrics.KiteBurstSeconds),5:F1}  "
                          + $"{rs2.Max(r => r.Metrics.KiteLongestSeconds),4:F1}");
        }
        sb.AppendLine();
        sb.AppendLine("  UPKEEP   variant          spCapped%v  spStarved%v  hpStones  errors v");
        foreach (var v in variants)
        {
            var s = Score.Of(v, all.Where(r => r.Variant.Name == v).ToList());
            sb.AppendLine($"           {s.Variant,-15} {s.SpCapped,10:F1}  {s.SpStarved,11:F1}  "
                          + $"{s.HpStones,8:F1}  {s.Errors,7}");
        }

        sb.AppendLine();
        sb.AppendLine("  DEAD TIME variant          total%v    walking  notEngaged  outOfReach  idle");
        foreach (var v in variants)
        {
            var rs = all.Where(r => r.Variant.Name == v).ToList();
            sb.AppendLine($"           {v,-15} {rs.Average(r => r.Metrics.DeadTimePercent),8:F1}  "
                          + $"{rs.Average(r => r.Metrics.WastedWalkingPercent),9:F1}  "
                          + $"{rs.Average(r => r.Metrics.WastedNotEngagedPercent),10:F1}  "
                          + $"{rs.Average(r => r.Metrics.WastedOutOfReachPercent),10:F1}  "
                          + $"{rs.Average(r => r.Metrics.WastedIdlePercent),4:F1}");
        }

        sb.AppendLine();
        sb.AppendLine("PER CELL  (mean over seeds)");
        foreach (var cell in all.Select(r => r.Cell).Distinct())
        {
            sb.AppendLine($"  {cell}");
            foreach (var v in variants)
            {
                var runs2 = all.Where(r => r.Cell == cell && r.Variant.Name == v).ToList();
                if (runs2.Count == 0) continue;
                var s = Score.Of(v, runs2);
                sb.AppendLine($"     {s.Variant,-15} kills {s.Kills,5:F1}  alive% {s.AlivePercent,5:F1}  "
                              + $"kills/min {s.KillsPerMinute,6:F2}  deaths {s.Deaths}  "
                              + $"dead% {s.DeadTimePercent,5:F1}  badKite% {s.BadKitePercent,5:F1}  "
                              + $"lowHp {s.LowWaterHp,3:F0}  errors {s.Errors}");
                sb.AppendLine($"     {"",-15}   dead time: walking {runs2.Average(r => r.Metrics.WastedWalkingPercent),4:F1} (closing {runs2.Average(r => r.Metrics.WastedWalkingCloserPercent),4:F1} kiting {runs2.Average(r => r.Metrics.WastedKitingWithAShotPercent),4:F1})  pastAnother {runs2.Average(r => r.Metrics.WastedChasingPastPercent),4:F1}  "
                              + $"notEngaged {runs2.Average(r => r.Metrics.WastedNotEngagedPercent),4:F1}  "
                              + $"outOfReach {runs2.Average(r => r.Metrics.WastedOutOfReachPercent),4:F1}  "
                              + $"idle {runs2.Average(r => r.Metrics.WastedIdlePercent),4:F1}");
            }
        }

        // A per-seed spread is what says whether a margin is real. Printed for the headline axis only.
        sb.AppendLine();
        sb.AppendLine("KILLS/MIN BY SEED  (a variant that only wins on one seed has not won)");
        foreach (var seed in all.Select(r => r.Seed).Distinct())
        {
            var parts = variants.Select(v =>
            {
                var rs = all.Where(r => r.Seed == seed && r.Variant.Name == v).ToList();
                return $"{v} {(rs.Count == 0 ? 0 : rs.Average(r => r.Result.KillsPerMinute)):F2}";
            });
            sb.AppendLine($"  seed {seed,-6} {string.Join("   ", parts)}");
        }
        return sb.ToString();
    }
}
