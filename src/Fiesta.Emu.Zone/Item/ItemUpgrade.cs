using Fiesta.Emu.Zone.Random;

namespace Fiesta.Emu.Zone.Item;

/// <summary>`SHINE_ITEM_STRUCT` -- the stored item. Only the fields the upgrade path touches.
///
/// <para>The binary keeps the level and the fail count at two different offset pairs depending on the item
/// class (attr+2/+4 or attr+0xa/+0xc). Both pairs hold the same two values, so this port holds them
/// once.</para></summary>
public sealed class ShineItemStruct
{
    public required int ItemId { get; set; }

    /// <summary>Written by `Item_AdjUpgradeLevel` (0x53B560).</summary>
    public byte UpgradeLevel { get; set; }

    /// <summary>Written by `Item_AdjFailCount` (0x53B700): reset to 0 on a success, +1 on every failure,
    /// per the call sites in `sp_NC_ITEM_NEW_UPGRADE_REQ`. Feeds the `nCon * failCount` term.</summary>
    public byte FailCount { get; set; }
}

/// <summary>`Item_Upgrade`'s return codes. The binary computes `bl` and returns `bl + 3`.</summary>
public enum ItemUpgradeResult : byte
{
    /// <summary>A lookup failed; several paths in the original return 0.</summary>
    Error = 0,
    AlreadyAtLimit = 1,
    BadUpgradeSource = 2,
    Success = 3,
    LuckySuccess = 4,
    FailNoChange = 5,
    FailDowngrade = 6,
    FailDestroyed = 7,
    BadLeftGem = 8,
    BadRightGem = 9,
    BadMiddleGem = 10,
}

/// <summary>An `ItemBag` slot view. <see cref="ItemUpgrade.NoSlot"/> means the slot argument is absent.</summary>
public interface IItemBag
{
    ShineItemStruct? At(byte slot);
}

/// <summary>The item enhancement chain from `Zone.exe`, ported 1:1.
///
/// <para>Slot roles are POSITIONAL: `Item_IsUpSourceLeftRight` takes no slot argument and accepts a red,
/// blue or gold gem in any of the three positions. The retail client locks them to one red, one gold and
/// one blue, in that order; the server does not check it. Left cancels a CriFail, right cancels a
/// DownFail, middle adds its UpSucRatio flat to the success side.</para></summary>
public static class ItemUpgrade
{
    public const byte NoSlot = 0xFF;

    /// <summary>`Item_GetUpgradeLimit` (0x53B2A0) -- the TARGET item's `ItemInfo.UpLimit`, its maximum
    /// enhancement level. The same column on a SOURCE item is the minimum level that source accepts; the
    /// field is read both ways depending on which side of the recipe the item sits on.</summary>
    public static byte Item_GetUpgradeLimit(ItemDataBox box, int itemId)
        => (byte)(box[itemId]?.UpLimit ?? 0);

    /// <summary>`Item_AdjUpgradeLevel` (0x53B560) -- `absolute` sets, otherwise adds (0xFF adds -1).</summary>
    public static byte Item_AdjUpgradeLevel(ShineItemStruct item, byte value, bool absolute)
    {
        item.UpgradeLevel = absolute ? value : unchecked((byte)(item.UpgradeLevel + value));
        return item.UpgradeLevel;
    }

    /// <summary>`Item_AdjFailCount` (0x53B700) -- same shape.</summary>
    public static byte Item_AdjFailCount(ShineItemStruct item, byte value, bool absolute)
    {
        item.FailCount = absolute ? value : unchecked((byte)(item.FailCount + value));
        return item.FailCount;
    }

    /// <summary>`Item_IsUpSource` (0x53C760) -- may this stone go in the CENTRE slot for this item at this
    /// level.
    ///
    /// <para>The grade test has a wildcard: a mismatch is rejected only when the stone's own `Grade` is
    /// non-zero. Karis has `Grade == 0`, which is the entirety of how it fits any grade -- there is no
    /// Karis-specific branch anywhere in the binary, and no level-60 or ItemLevel gate.</para></summary>
    public static bool Item_IsUpSource(ItemDataBox box, int targetId, int sourceId, byte level)
    {
        var source = box[sourceId];
        if (source is null || source.Class != ItemClassEnum.UpgradeSource) return false;

        var target = box[targetId];
        if (target is null) return false;

        if (target.Class == ItemClassEnum.Bracelet)
        {
            // Bracelets take only the four BR_UPSORCE0n sources, matched by id.
            if (!box.SpecialId.BraceletUpSource.Contains(sourceId)) return false;
        }
        else if (target.Grade != source.Grade && source.Grade != 0)
        {
            return false;
        }

        return level >= source.UpLimit && level <= source.UpResource;
    }

    /// <summary>`Item_IsUpSourceLeftRight` (0x53B3E0) -- may this gem go in a left/right/middle slot. No
    /// slot argument: any of the three gem classes is accepted in any position. Unlike the centre stone
    /// there is NO zero-grade wildcard here.</summary>
    public static bool Item_IsUpSourceLeftRight(ItemDataBox box, int targetId, int gemId, byte level)
    {
        var gem = box[gemId];
        if (gem is null) return false;
        if (gem.Class != ItemClassEnum.UpgradeRedGem
            && gem.Class != ItemClassEnum.UpgradeGoldGem
            && gem.Class != ItemClassEnum.UpgradeBlueGem) return false;

        var target = box[targetId];
        if (target is null || target.Grade != gem.Grade) return false;

        return level >= gem.UpLimit && level <= gem.UpResource;
    }

    /// <summary>`Item_Upgrade` (0x53CA10). Returns the outcome; it does NOT mutate the item -- the packet
    /// handler applies the result, which <see cref="Apply"/> mirrors.</summary>
    public static ItemUpgradeResult Item_Upgrade(
        ItemDataBox box, UpgradeTables tables, cWell512Random rng, IItemBag bag,
        byte targetSlot, byte sourceSlot, byte leftSlot, byte rightSlot, byte middleSlot)
    {
        var item = bag.At(targetSlot);
        if (item is null) return ItemUpgradeResult.Error;

        var limit = Item_GetUpgradeLimit(box, item.ItemId);
        if (limit == 0) return ItemUpgradeResult.Error;

        var targetInfo = box[item.ItemId];
        if (targetInfo is null) return ItemUpgradeResult.Error;

        var itemClass = targetInfo.Class;
        if ((uint)(itemClass - 4) > 0x22) return ItemUpgradeResult.Error;

        var level = item.UpgradeLevel;
        int failCount = item.FailCount;

        // cmp [ebp+0xf], al / ja -- the limit must be STRICTLY above the current level.
        if (limit <= level) return ItemUpgradeResult.AlreadyAtLimit;

        var source = bag.At(sourceSlot);
        if (source is null) return ItemUpgradeResult.Error;
        if (!Item_IsUpSource(box, item.ItemId, source.ItemId, level))
            return ItemUpgradeResult.BadUpgradeSource;
        var sourceInfo = box[source.ItemId];
        if (sourceInfo is null) return ItemUpgradeResult.Error;

        // The pity term is scaled by WHICH side slots are filled: 13 for left, 12 for right, then /10 --
        // left only 1.3x, right only 1.2x, both 2.5x, neither unchanged. The scaled value is a LOCAL; the
        // gems never write back to the item's stored fail count.
        var scale = 0;
        ItemInfo? leftInfo = null, rightInfo = null, middleInfo = null;

        if (leftSlot != NoSlot)
        {
            var gem = bag.At(leftSlot);
            if (gem is null) return ItemUpgradeResult.Error;
            if (!Item_IsUpSourceLeftRight(box, item.ItemId, gem.ItemId, level))
                return ItemUpgradeResult.BadLeftGem;
            leftInfo = box[gem.ItemId];
            scale += failCount * 13;
        }

        if (rightSlot != NoSlot)
        {
            var gem = bag.At(rightSlot);
            if (gem is null) return ItemUpgradeResult.Error;
            if (!Item_IsUpSourceLeftRight(box, item.ItemId, gem.ItemId, level))
                return ItemUpgradeResult.BadRightGem;
            rightInfo = box[gem.ItemId];
            scale += failCount * 12;
        }

        var scaled = scale / 10;
        if (scaled != 0) failCount = (byte)scaled;

        if (middleSlot != NoSlot)
        {
            var gem = bag.At(middleSlot);
            if (gem is null) return ItemUpgradeResult.Error;
            if (!Item_IsUpSourceLeftRight(box, item.ItemId, gem.ItemId, level))
                return ItemUpgradeResult.BadMiddleGem;
            middleInfo = box[gem.ItemId];
        }

        // -- the odds row --------------------------------------------------------------------------
        AccUpgrade? row;
        if (itemClass == ItemClassEnum.Bracelet)
        {
            row = tables.GetBRAccUpgrade(targetInfo.Grade, targetInfo.DemandLv, level);
        }
        else
        {
            row = tables.GetItemUpgrade(targetInfo.Grade, level);
            // Class 4 substitutes AccUpGradeTable at the SAME index, keeping the ItemUpgrade row if the
            // accessory table has no such entry.
            if (itemClass == ItemClassEnum.Amulet)
                row = tables.GetByIndex(UpgradeTables.IndexOf(targetInfo.Grade, level)) ?? row;
        }
        if (row is null) return ItemUpgradeResult.Error;

        int criFail = row.CriFail, downFail = row.DownFail, normalFail = row.NormalFail, nCon = row.nCon;

        // -- the success side ----------------------------------------------------------------------
        var sucRatio = sourceInfo.UpSucRatio + (middleInfo?.UpSucRatio ?? 0);
        var demandLv = targetInfo.DemandLv;
        var flat = 120;                                   // mov edi, 0x78
        var bothGrade6 = targetInfo.Grade >= 6 && sourceInfo.Grade >= 6;

        // Hardcoded grade-6 carve-out (0x53CF85), non-bracelet only: the pity term, the flat 120, the
        // demand-level penalty and CriFail are zeroed; DownFail too when the stone's ItemGradeType is 4.
        if (itemClass != ItemClassEnum.Bracelet && bothGrade6)
        {
            flat = 0;
            criFail = 0;
            nCon = 0;
            failCount = 0;
            demandLv = 0;
            if (sourceInfo.ItemGradeType == 4) downFail = 0;
        }

        var failSum = criFail + downFail + normalFail;
        var threshold = failSum - (nCon * failCount - demandLv + flat + sucRatio);

        var roll = (int)rng.well512_GetRandom(1000);

        // Two overrides at 0x53CFE7/0x53CFFC, applied in this order and reached by every path.
        if (itemClass == ItemClassEnum.Bracelet)
        {
            threshold = normalFail - sucRatio;
            if (threshold < 0) threshold = 0;
        }
        if (bothGrade6) threshold = normalFail;

        if (roll > threshold)
            return LuckyOrPlain(rng, targetInfo, sourceInfo, itemClass,
                                leftInfo, rightInfo, middleInfo, level, limit);

        // -- which failure -------------------------------------------------------------------------
        var second = (int)rng.well512_GetRandom(1000);

        int normCri, normDown;
        if (failSum > 0)
        {
            normCri = (ushort)(criFail * 1000 / failSum);
            // The both-grade-6 case leaves DownFail RAW rather than renormalising it.
            normDown = bothGrade6 && itemClass != ItemClassEnum.Bracelet
                ? downFail
                : (ushort)(downFail * 1000 / failSum);
        }
        else
        {
            normCri = criFail;
            normDown = downFail;
        }

        if (second < normCri)
        {
            // CriFail -- destroyed, unless the LEFT slot's gem saves it on its own roll.
            if (leftInfo is not null && rng.well512_GetRandom(1000) < leftInfo.UpSucRatio)
                return ItemUpgradeResult.FailNoChange;
            return ItemUpgradeResult.FailDestroyed;
        }

        if (second < normCri + normDown)
        {
            // DownFail -- minus one level, unless the RIGHT slot's gem saves it.
            if (rightInfo is not null && rng.well512_GetRandom(1000) < rightInfo.UpSucRatio)
                return ItemUpgradeResult.FailNoChange;
            // At +0 there is nothing to take away, so it degrades to "nothing happened".
            return level > 0 ? ItemUpgradeResult.FailDowngrade : ItemUpgradeResult.FailNoChange;
        }

        return ItemUpgradeResult.FailNoChange;
    }

    /// <summary>The success tail at 0x53D127 -- a SECOND roll, taken only once the attempt has already
    /// succeeded, that turns +1 into +2.
    ///
    /// <para>The luck pool sums `UpLuckRatio` from the centre stone AND all three gem slots. A grade 6+
    /// item with a grade 6+ stone skips the roll entirely; bracelets always take it.</para></summary>
    private static ItemUpgradeResult LuckyOrPlain(
        cWell512Random rng, ItemInfo targetInfo, ItemInfo sourceInfo, int itemClass,
        ItemInfo? left, ItemInfo? right, ItemInfo? middle, byte level, byte limit)
    {
        var pool = sourceInfo.UpLuckRatio
                   + (left?.UpLuckRatio ?? 0)
                   + (right?.UpLuckRatio ?? 0)
                   + (middle?.UpLuckRatio ?? 0);

        if (itemClass != ItemClassEnum.Bracelet
            && targetInfo.Grade >= 6 && sourceInfo.Grade >= 6)
            return ItemUpgradeResult.Success;

        if (pool <= 0) return ItemUpgradeResult.Success;
        if (rng.well512_GetRandom(1000) >= pool) return ItemUpgradeResult.Success;

        // +2 only when it fits under the item's own UpLimit; otherwise the lucky roll is spent for +1.
        return level + 2 <= limit ? ItemUpgradeResult.LuckySuccess : ItemUpgradeResult.Success;
    }

    /// <summary>What the packet handler does with the outcome: `Item_AdjUpgradeLevel` then
    /// `Item_AdjFailCount`. Success RESETS the fail count; every failure adds one.</summary>
    public static void Apply(ShineItemStruct item, ItemUpgradeResult result)
    {
        switch (result)
        {
            case ItemUpgradeResult.Success:
                Item_AdjUpgradeLevel(item, 1, absolute: false);
                Item_AdjFailCount(item, 0, absolute: true);
                break;
            case ItemUpgradeResult.LuckySuccess:
                Item_AdjUpgradeLevel(item, 2, absolute: false);
                Item_AdjFailCount(item, 0, absolute: true);
                break;
            case ItemUpgradeResult.FailNoChange:
                Item_AdjUpgradeLevel(item, 0, absolute: false);
                Item_AdjFailCount(item, 1, absolute: false);
                break;
            case ItemUpgradeResult.FailDowngrade:
                Item_AdjUpgradeLevel(item, 0xFF, absolute: false);
                Item_AdjFailCount(item, 1, absolute: false);
                break;
            case ItemUpgradeResult.FailDestroyed:
                item.ItemId = 0;
                break;
        }
    }
}
