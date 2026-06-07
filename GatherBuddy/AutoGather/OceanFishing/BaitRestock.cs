using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.AutoGather.OceanFishing;

// Pre-embark bait inventory check + restock orchestration.
//
// Approach: we don't own the vendor mapping (NPC→shop→item→price) — that surface already exists in
// the existing VendorBuyListManager / VendorPurchaseManager pipeline, and the user can tune which
// NPC to buy from per bait via the Vulcan UI. PR 6 only adds: a defaulted target list of ocean
// baits, an inventory probe, and a trigger that starts the user's chosen buy list if any required
// bait is below threshold. EmbarkController gates on this completing before Pathing.
//
// Ocean fishing bait item ids (verified via XIVAPI v2 on 2026-06-07):
//   29714 Ragworm
//   29715 Krill
//   29716 Plump Worm
//   29717 Versatile Lure
//   12704 Stonefly Nymph
// The original numbers in this file (27590, 2603, 2587, 29714, 29715) were a complete misread —
// they pointed at unrelated items, so the inventory probe always reported 0 for every bait.
public sealed class BaitRestock
{
    public sealed record BaitTarget(uint ItemId, string Name, int LowThreshold, int RestockTarget);

    public bool Enabled { get; set; } = false;

    // If set, BaitRestock will trigger VendorBuyListManager.Start(BuyListId) on demand. The user is
    // expected to have configured that list with the right NPC / shop / quantities — we don't try
    // to construct it for them in PR 6.
    public Guid? BuyListId { get; set; }

    public List<BaitTarget> Targets { get; } = new()
    {
        new(29715, "Krill",          200, 999),
        new(29716, "Plump Worm",     200, 999),
        new(29714, "Ragworm",        200, 999),
        new(29717, "Versatile Lure",  50, 199),
        new(12704, "Stonefly Nymph", 100, 499),
    };

    public string LastStatus { get; private set; } = string.Empty;

    public IEnumerable<(BaitTarget Target, int Have)> Snapshot()
        => Targets.Select(t => (t, GetInventoryCount(t.ItemId)));

    public bool AnyMissing()
        => Snapshot().Any(s => s.Have < s.Target.LowThreshold);

    public enum RestockResult { NotNeeded, NotConfigured, Started, AlreadyRunning, Failed }

    // Kick off the configured buy list run. Returns immediately; poll IsRunning() to know when to
    // proceed.
    public RestockResult TryStart()
    {
        if (!Enabled)
            return RestockResult.NotNeeded;
        if (!AnyMissing())
        {
            LastStatus = "All baits above threshold; no restock needed.";
            return RestockResult.NotNeeded;
        }
        if (BuyListId is not { } id)
        {
            LastStatus = "BuyListId not configured. Build the bait list in the Vulcan vendor UI and assign it here.";
            return RestockResult.NotConfigured;
        }

        var mgr = GatherBuddy.VendorBuyListManager;
        if (mgr.IsRunning)
        {
            LastStatus = $"VendorBuyListManager already running: {mgr.StatusText}";
            return RestockResult.AlreadyRunning;
        }

        var result = mgr.Start(id);
        LastStatus = $"VendorBuyListManager.Start → {result}";
        return result switch
        {
            VendorBuyListManager.StartResult.AlreadyRunning => RestockResult.AlreadyRunning,
            VendorBuyListManager.StartResult.NoList         => RestockResult.Failed,
            _ => RestockResult.Started,
        };
    }

    public bool IsRunning => GatherBuddy.VendorBuyListManager?.IsRunning ?? false;

    public string RunnerStatus => GatherBuddy.VendorBuyListManager?.StatusText ?? string.Empty;

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
                var container = inventory->GetInventoryContainer(inv);
                if (container == null) continue;
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0) continue;
                    if (slot->ItemId == itemId) total += (int)slot->Quantity;
                }
            }
            return total;
        }
        catch { return 0; }
    }
}
