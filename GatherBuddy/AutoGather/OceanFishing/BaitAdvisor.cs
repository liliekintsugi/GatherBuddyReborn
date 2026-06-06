using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Classes;

namespace GatherBuddy.AutoGather.OceanFishing;

// PR 7a — read-only bait advisory.
//
// Walks the user's active and fallback AutoGather items, keeps only Fish entries (skips spearfish
// — those need gigs, not baits), groups by Fish.InitialBait, and reports `(bait, fishCount,
// haveQty)` so the UI can flag missing baits. A one-click "Add to active buy list" hands off to
// VendorBuyListManager.TryIncrementTarget — the existing vendor pipeline does the NPC / shop / price
// resolution.
//
// PR 7a stops at "advise + queue". The actual purchase is still triggered by the user from the
// vendor UI. PR 7b will intercept the AutoGather state machine to detour automatically.
public static class BaitAdvisor
{
    public sealed record MissingBait(
        uint                       BaitItemId,
        string                     BaitName,
        ushort                     IconId,
        int                        HaveQty,
        int                        DesiredQty,
        IReadOnlyList<string>      FishNames);

    public const int DefaultDesiredQtyPerFish = 99;

    // Scan active + fallback lists. `desiredPerFish` is a heuristic — each fish in the list
    // contributes `desiredPerFish` units of its required bait to the target stockpile.
    public static List<MissingBait> Scan(int desiredPerFish = DefaultDesiredQtyPerFish)
    {
        var mgr = GatherBuddy.AutoGatherLists;
        if (mgr == null)
            return [];

        var perBait = new Dictionary<uint, BaitAccumulator>();

        foreach (var (item, _) in mgr.ActiveItems.Concat(mgr.FallbackItems))
        {
            if (item is not Fish fish)
                continue;
            if (fish.IsSpearFish)
                continue;

            var bait = fish.InitialBait;
            if (bait == null || bait.Id == 0)
                continue;

            if (!perBait.TryGetValue(bait.Id, out var acc))
            {
                acc = new BaitAccumulator(bait.Id, bait.Name ?? $"#{bait.Id}", bait.Icon);
                perBait[bait.Id] = acc;
            }

            acc.Fishes.Add(fish.Name[ClientLanguage.English]);
        }

        var results = new List<MissingBait>(perBait.Count);
        foreach (var acc in perBait.Values)
        {
            var have    = GetInventoryCount(acc.BaitId);
            var desired = acc.Fishes.Count * desiredPerFish;
            results.Add(new MissingBait(acc.BaitId, acc.Name, acc.Icon, have, desired, acc.Fishes));
        }

        return results
            .OrderBy(r => r.HaveQty >= r.DesiredQty ? 1 : 0)   // missing first
            .ThenBy(r => r.BaitName)
            .ToList();
    }

    // Push one missing bait into the user's active vendor buy list. Quantity = max(0, desired-have).
    public static bool QueueRestock(MissingBait bait)
    {
        var need = (uint)System.Math.Max(0, bait.DesiredQty - bait.HaveQty);
        if (need == 0)
            return false;
        return GatherBuddy.VendorBuyListManager.TryIncrementTarget(bait.BaitItemId, need);
    }

    public static int QueueAllMissing(IEnumerable<MissingBait> baits)
    {
        var count = 0;
        foreach (var b in baits)
            if (b.HaveQty < b.DesiredQty && QueueRestock(b))
                count++;
        return count;
    }

    private sealed class BaitAccumulator
    {
        public BaitAccumulator(uint id, string name, ushort icon) { BaitId = id; Name = name; Icon = icon; }
        public uint   BaitId { get; }
        public string Name   { get; }
        public ushort Icon   { get; }
        public List<string> Fishes { get; } = new();
    }

    private static unsafe int GetInventoryCount(uint itemId)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null) return 0;
            InventoryType[] inventories =
            [
                InventoryType.Inventory1, InventoryType.Inventory2,
                InventoryType.Inventory3, InventoryType.Inventory4,
            ];
            var total = 0;
            foreach (var inv in inventories)
            {
                var c = inventory->GetInventoryContainer(inv);
                if (c == null) continue;
                for (var i = 0; i < c->Size; i++)
                {
                    var slot = c->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0) continue;
                    if (slot->ItemId == itemId) total += (int)slot->Quantity;
                }
            }
            return total;
        }
        catch { return 0; }
    }
}
