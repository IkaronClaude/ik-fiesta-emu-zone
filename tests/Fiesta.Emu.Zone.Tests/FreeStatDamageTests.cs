using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>The per-rule `roe_Damage` override: a FLAT free-stat term this port did not have.
///
/// <para>`roe_Damage` is overridden at vtable slot 4 by five of the eight rules. The port had the base
/// function and treated it as the whole story, which is why its damage ceiling sat a few percent under a
/// real capture's.</para>
///
/// <para>Verified by EXECUTION, not by reading: `tools/oracle_free_stat_damage.py` stubs the two accessors
/// and runs the real function under emulation over six input sets, every one exact.</para></summary>
public class FreeStatDamageTests
{
    private static Combatant Fighter(int level = 61)
    {
        var p = new ParameterContainer();
        p.Base[Stat.Str] = 200;
        p.Base[Stat.Con] = 150;
        p.Base[Stat.Int] = 120;
        p.Base[Stat.Men] = 130;
        p.Base[Stat.WCmin] = 800;
        p.Base[Stat.WCmax] = 900;
        p.Base[Stat.MAmin] = 700;
        p.Base[Stat.MAmax] = 800;
        p.Base[Stat.AC] = 200;
        p.Base[Stat.MR] = 180;
        return new Combatant(level, p);
    }

    /// <summary>The whole term: attacker's spent points ADD, defender's SUBTRACT, one damage per point.
    /// Both are the same six figures `tools/oracle_free_stat_damage.py` gets out of the real function.</summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(20, 0, +20)]
    [InlineData(0, 20, -20)]
    [InlineData(30, 7, +23)]
    [InlineData(5, 60, -55)]
    [InlineData(255, 255, 0)]
    public void TheFreeStatTermIsAttackerMinusDefender(int attackerFree, int defenderFree, int expected)
    {
        var a = Fighter();
        var d = Fighter();
        var plain = new AttackModifiers { RollPermille = 500, ForceCritical = false };

        var baseline = DamageCalculator.ResolveDamage(a, d, plain);
        var withTerm = DamageCalculator.ResolveDamage(a, d, plain with
        {
            AttackerFreeStat = attackerFree,
            DefenderFreeStat = defenderFree,
        });

        (withTerm - baseline).ShouldBe(expected);
    }

    /// <summary>Which rules carry it, read off the vtables. <b>AlwaysCritical takes the PHYSICAL
    /// override</b> — the one nobody would guess — while the three healing/always-hit rules keep the base
    /// function and get no free-stat term at all.</summary>
    [Fact]
    public void OnlyFiveOfTheEightRulesCarryTheTerm()
    {
        EngagementRule.NormalPhysical.FreeStatSchool().ShouldBe(DamageSchool.Physical);
        EngagementRule.PhysicalSkill.FreeStatSchool().ShouldBe(DamageSchool.Physical);
        EngagementRule.AlwaysCritical.FreeStatSchool().ShouldBe(DamageSchool.Physical,
            "AlwaysCritical's slot 4 is roe_Damage@NormalPY, which is not obvious from its name");

        EngagementRule.NormalMagic.FreeStatSchool().ShouldBe(DamageSchool.Magical);
        EngagementRule.MagicalSkill.FreeStatSchool().ShouldBe(DamageSchool.Magical);

        EngagementRule.CureSkill.FreeStatSchool().ShouldBeNull();
        EngagementRule.AlwaysHit.FreeStatSchool().ShouldBeNull();
        EngagementRule.HealAttack.FreeStatSchool().ShouldBeNull();
    }

    /// <summary>A rule that keeps the base function ignores the term even when one is supplied.</summary>
    [Fact]
    public void ARuleWithoutTheOverrideIgnoresIt()
    {
        var a = Fighter();
        var d = Fighter();
        var mods = new AttackModifiers
        {
            RollPermille = 500, ForceCritical = false,
            AttackerFreeStat = 100, DefenderFreeStat = 0,
        };

        foreach (var rule in new[] { EngagementRule.CureSkill, EngagementRule.AlwaysHit, EngagementRule.HealAttack })
            DamageCalculator.ResolveDamage(a, d, mods, rule: rule)
                .ShouldBe(DamageCalculator.ResolveDamage(a, d, mods with { AttackerFreeStat = 0 }, rule: rule),
                    $"{rule} keeps the base roe_Damage");
    }

    /// <summary>⚠️ It is added BEFORE the critical doubling, so a critical doubles the free-stat term too.
    /// That is the order in the original — the override runs inside `roe_Damage`, and `roe_CalcDamage`
    /// doubles afterwards.</summary>
    [Fact]
    public void ACriticalDoublesTheFreeStatTermToo()
    {
        var a = Fighter();
        var d = Fighter();
        var mods = new AttackModifiers { RollPermille = 500, AttackerFreeStat = 50 };

        var normal = DamageCalculator.ResolveDamage(a, d, mods with { ForceCritical = false });
        var crit = DamageCalculator.ResolveDamage(a, d, mods with { ForceCritical = true });

        crit.ShouldBe(normal * 2, "the doubling wraps the whole roe_Damage result, term included");
    }
}
