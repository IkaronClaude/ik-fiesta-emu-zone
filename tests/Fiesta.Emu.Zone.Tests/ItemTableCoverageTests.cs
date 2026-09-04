using Fiesta.Emu.Zone.Data;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>⭐ EVERY SERVER TABLE KEYED BY AN ITEM MUST BE ACCOUNTED FOR — modelled, or dismissed in
/// writing with a reason.
///
/// <para>⚠️ <b>This is the guard for a mistake that has now been made three times on the same nine
/// items.</b> Rebuilding `MageDamageLvl60`'s character, this port dumped every non-zero column of
/// `ItemInfo.shn`, found no STR/CON/DEX/INT/MEN, and concluded items cannot grant primary stats. They can:
/// `GradeItemOption.shn` holds them, keyed by `InxName`, and a Mini Phino pet was quietly adding +2 to
/// every stat. `ItemInfo.shn` has no primary-stat column for ANY item — which is the signature of reading
/// the wrong table, not of the answer being no.</para>
///
/// <para><b>An absent COLUMN is evidence about a schema. Only an absent VALUE in the right table is
/// evidence about the entity.</b></para>
///
/// <para>Resolving to be more careful does not survive a context window, so the check is mechanical:
/// <see cref="ItemTableDiscovery"/> finds what is keyed by an item and this test fails on anything the
/// list below does not name. Adding a row is cheap; being unable to add one honestly is the signal.</para></summary>
public class ItemTableCoverageTests(ITestOutputHelper output)
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "ItemInfo.shn")) ? root : null;
    }

    /// <summary>What each item-keyed table is, and whether the damage engine reads it.
    ///
    /// <para><b>MODELLED</b> means something in `Fiesta.Emu.Zone` loads it. Everything else carries the
    /// reason it cannot change a character's combat numbers — and "I could not find a reader" is NOT such
    /// a reason. The bar is a statement about the DATA.</para></summary>
    private static readonly Dictionary<string, string> Accounted = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---- modelled --------------------------------------------------------------------------------
        ["ItemInfo.shn"] = "MODELLED - EquipmentCatalog: AC/MR/WC/MA/TH/TB/CriRate and the rate columns.",
        ["GradeItemOption.shn"] = "MODELLED - EquipmentCatalog joins it by InxName for STR/CON/DEX/INT/MEN. "
            + "The pet's +2-to-everything, verified against an equip->CHANGEPARAM step on the wire.",
        ["ActiveSkill.shn"] = "MODELLED - SkillCatalog: the skill damage columns and empower tables.",
        ["ActiveSkillInfoServer.shn"] = "MODELLED - SkillCatalog: hit rate and the rules-object selector.",
        ["PassiveSkill.shn"] = "MODELLED - weapon mastery feeds the damage bands.",
        ["MobWeapon.shn"] = "MODELLED - a mob's weapon choice, which is how mob damage gets its WC.",

        // ---- read, not yet modelled ------------------------------------------------------------------
        ["ChargedEffect.shn"] = "OPEN - cash-shop item effects, {EffectEnum, EffectValue, StaStrength}. "
            + "The capture's four cosmetics carry EffectEnum 9, which was checked as a candidate for the "
            + "unexplained +5% all-stat residual and RULED OUT: enum 9 is 3,692 of the table's 4,546 rows "
            + "and takes only the values 0 and 1, so it is a costume flag, not a bonus. The enums that do "
            + "carry rates are permille and single-purpose (13 HPIncrease, 14 SPIncrease, 15 DIncrease, "
            + "10-12 A/D/AD Power), none of which touches all five primaries. Still OPEN because the "
            + "rate-bearing enums are unmodelled, not because of the residual.",
        ["StateItem.shn"] = "OPEN - items that apply an abstate. None of the capture's nine appear in it, "
            + "so it does not explain the residual, but an abstate CAN carry stats and this is unread.",
        ["ItemUseEffect.shn"] = "OPEN - consumable use effects. Affects potions/scrolls, not worn gear.",
        ["ToggleSkill.shn"] = "OPEN - toggled skills. None learned by the capture's character.",
        ["PSkillSetAbstate.shn"] = "OPEN - passive-skill abstates; 16 rows, none for this character's 18.",
        ["ItemInfoServer.shn"] = "NOT COMBAT - drop groups, market index, vanish/looting timers, KQ flags. "
            + "Checked column by column for the capture's nine items: no stat column exists in it.",

        // ---- not combat-relevant ---------------------------------------------------------------------
        ["MinimonInfo.shn"] = "NOT COMBAT - {MinimonEquipPos, MinimonRole} only. A pet's STATS are in "
            + "GradeItemOption; this table says where it sits and what it does, not what it grants.",
        ["MinimonAutoUseItem.shn"] = "NOT COMBAT - which consumable a pet auto-uses.",
        ["ActionEffectItem.shn"] = "NOT COMBAT - client-side action effects.",
        ["ActionViewInfo.shn"] = "NOT COMBAT - client-side animation.",
        ["ActiveSkillGroup.shn"] = "NOT COMBAT - skill grouping for the UI.",
        ["AttendReward.shn"] = "NOT COMBAT - attendance reward payouts.",
        ["ChargedDeletableBuff.shn"] = "NOT COMBAT - which cash buffs may be cancelled.",
        ["CollectCard.shn"] = "NOT COMBAT - collection card contents.",
        ["CollectCardReward.shn"] = "NOT COMBAT - collection rewards.",
        ["CollectCardTitle.shn"] = "NOT COMBAT - titles from collections; the capture's character has none "
            + "(NC_CHAR_CLIENT_CHARTITLE_CMD reports CurrentTitle 0, NumOfTitle 0).",
        ["FriendPointReward.shn"] = "NOT COMBAT - friend-point payouts.",
        ["GBReward.shn"] = "NOT COMBAT - guild-battle payouts.",
        ["Gather.shn"] = "NOT COMBAT - gathering tools.",
        ["HolyPromiseReward.shn"] = "NOT COMBAT - event payouts.",
        ["ItemDropLog.shn"] = "NOT COMBAT - which drops get logged.",
        ["ItemInvenDel.shn"] = "NOT COMBAT - items cleaned out of inventories.",
        ["ItemMix.shn"] = "NOT COMBAT - crafting recipes.",
        ["ItemMoney.shn"] = "NOT COMBAT - currency items.",
        ["ItemPackage.shn"] = "NOT COMBAT - package contents.",
        ["ItemShop.shn"] = "NOT COMBAT - shop stock.",
        ["JobEquipInfo.shn"] = "NOT COMBAT - the starter kit each job is given.",
        ["KQItem.shn"] = "NOT COMBAT - kingdom-quest items.",
        ["KingdomQuestRew.shn"] = "NOT COMBAT - kingdom-quest payouts.",
        ["LCGroupRate.shn"] = "NOT COMBAT - lucky-chest rates.",
        ["LCReward.shn"] = "NOT COMBAT - lucky-chest payouts.",
        ["MiniHouse.shn"] = "NOT COMBAT - housing.",
        ["MiniHouseFurniture.shn"] = "NOT COMBAT - housing.",
        ["MiniHouseFurnitureObjEffect.shn"] = "NOT COMBAT - housing.",
        ["MiniHouseObjAni.shn"] = "NOT COMBAT - housing animation.",
        ["MoverHG.shn"] = "NOT COMBAT - mount hunger/feeding.",
        ["MoverItem.shn"] = "NOT COMBAT - mount item mapping.",
        ["MoverMain.shn"] = "NOT COMBAT - mount definitions. Mounts change movement, not damage.",
        ["MysteryVaultServer.shn"] = "NOT COMBAT - vault contents.",
        ["Produce.shn"] = "NOT COMBAT - production recipes.",
        ["PupCaseDesc.shn"] = "NOT COMBAT - pup case descriptions.",
        ["PupMain.shn"] = "NOT COMBAT - pup definitions.",
        ["PupServer.shn"] = "NOT COMBAT - pup server flags.",
        ["RareMoverEachRate.shn"] = "NOT COMBAT - rare mount rates.",
        ["Riding.shn"] = "NOT COMBAT - riding definitions.",
        ["ShineReward.shn"] = "NOT COMBAT - generic reward payouts.",
        ["TermExtendMatch.shn"] = "NOT COMBAT - which item extends which timed item.",
    };

    /// <summary>⭐ THE GUARD. A table keyed by an item that nobody has written a line about fails here.</summary>
    [SkippableFact]
    public void EveryItemKeyedTableIsAccountedFor()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var discovered = ItemTableDiscovery.Tables(shine!);
        output.WriteLine($"{discovered.Count} item-keyed tables discovered, {Accounted.Count} accounted for");

        var unaccounted = discovered.Where(t => !Accounted.ContainsKey(t)).OrderBy(t => t).ToList();
        foreach (var t in unaccounted) output.WriteLine($"  UNACCOUNTED: {t}");

        unaccounted.ShouldBeEmpty(
            "a server table is keyed by an item and nothing says what it does. Read it, then add a line to "
            + "`Accounted` saying either that it is modelled or -- with a statement about the DATA, not "
            + "about what you could find -- why it cannot change a character's combat numbers. This test "
            + "exists because 'no such column in ItemInfo.shn' was once read as 'items have no such "
            + "property', and a pet's +2 to every stat went unmodelled for three sessions");
    }

    /// <summary>...and the reverse: a name in the list that the scan no longer finds is a stale entry,
    /// which quietly makes the guard weaker than it looks.</summary>
    [SkippableFact]
    public void NothingIsAccountedForThatDoesNotExist()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var discovered = ItemTableDiscovery.Tables(shine!);
        var stale = Accounted.Keys
            .Where(t => !discovered.Contains(t)
                        && !t.Equals("ItemInfo.shn", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t).ToList();

        foreach (var t in stale) output.WriteLine($"  STALE: {t}");
        stale.ShouldBeEmpty("these are listed as accounted for but the scan no longer finds them");
    }

    /// <summary>⚠️ AND THE ONE THAT WOULD HAVE CAUGHT IT. `ItemInfo.shn` does not carry primary stats for
    /// any item in the game — asserting that in a test turns a silent blind spot into a stated fact, and
    /// names the file that does carry them.</summary>
    [SkippableFact]
    public void PrimaryStatsAreNotInItemInfoAtAll()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var columns = ShnFile.Load(Path.Combine(shine!, "ItemInfo.shn")).Rows[0].Keys
            .Select(c => c.ToUpperInvariant()).ToHashSet();

        foreach (var stat in new[] { "STR", "CON", "DEX", "INT", "MEN" })
            columns.ShouldNotContain(stat,
                "if ItemInfo ever grows a primary-stat column, EquipmentCatalog must read it there too -- "
                + "today they come from GradeItemOption.shn and nowhere else");

        var grade = ShnFile.Load(Path.Combine(shine!, "GradeItemOption.shn")).Rows[0].Keys
            .Select(c => c.ToUpperInvariant()).ToHashSet();
        foreach (var stat in new[] { "STR", "CON", "DEX", "INT", "MEN" })
            grade.ShouldContain(stat, "GradeItemOption.shn is where an item's primary stats live");
    }
}
