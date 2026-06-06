using System;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.AutoGather.OceanFishing;

// PR 7b — background guard that keeps fishing baits in stock while AutoGather is running.
//
// We don't surgically pause AutoGather: when the vendor list automation kicks in, AutoGather's own
// actions stop having effect (NPC dialog steals focus, AutoGather sees something is busy). When the
// vendor run finishes, AutoGather resumes naturally. So all this controller does is:
//   - periodically reuse BaitAdvisor.Scan() to detect missing baits across active+fallback lists,
//   - queue them via VendorBuyListManager.TryIncrementTarget(...),
//   - call VendorBuyListManager.Start() if not already running.
//
// The user keeps full control via Enabled / interval / threshold.
public sealed class BaitGuard
{
    public bool Enabled { get; set; } = false;

    // Trigger only if AutoGather is currently running (Enabled). If false, we run the check whenever
    // the plugin is loaded.
    public bool OnlyWhenAutoGatherEnabled { get; set; } = true;

    // Only consider baits whose count is below this fraction of the desired quantity (0..1).
    // Default 0.5 = "if I've burned through half, restock". Avoids constant noise.
    public float TriggerBelowFraction { get; set; } = 0.5f;

    public int DesiredQtyPerFish { get; set; } = BaitAdvisor.DefaultDesiredQtyPerFish;

    // Check at most this often.
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(15);

    public DateTime LastCheckUtc      { get; private set; } = DateTime.MinValue;
    public string   LastStatus        { get; private set; } = string.Empty;
    public int      LastQueuedCount   { get; private set; }
    public int      TotalQueuedCount  { get; private set; }

    public void Tick()
    {
        if (!Enabled)
            return;
        if (OnlyWhenAutoGatherEnabled && GatherBuddy.AutoGather?.Enabled != true)
            return;
        if (DateTime.UtcNow - LastCheckUtc < CheckInterval)
            return;
        LastCheckUtc = DateTime.UtcNow;

        try
        {
            var snap = BaitAdvisor.Scan(DesiredQtyPerFish);
            if (snap.Count == 0)
            {
                LastStatus = "No fish targets in active/fallback lists.";
                return;
            }

            var queued = 0;
            foreach (var b in snap)
            {
                if (b.DesiredQty <= 0) continue;
                var threshold = (int)(b.DesiredQty * TriggerBelowFraction);
                if (b.HaveQty >= threshold) continue;
                if (BaitAdvisor.QueueRestock(b))
                    queued++;
            }

            LastQueuedCount = queued;
            TotalQueuedCount += queued;

            if (queued == 0)
            {
                LastStatus = $"All baits above {TriggerBelowFraction:P0} threshold.";
                return;
            }

            // Kick the vendor list if it's not already running.
            var mgr = GatherBuddy.VendorBuyListManager;
            if (!mgr.IsRunning)
            {
                var res = mgr.Start();
                LastStatus = $"Queued {queued} bait(s); VendorBuyListManager.Start → {res}.";
                GatherBuddy.Log.Information($"[BaitGuard] {LastStatus}");
            }
            else
            {
                LastStatus = $"Queued {queued} bait(s); vendor pipeline already running.";
            }
        }
        catch (Exception e)
        {
            LastStatus = $"check failed: {e.Message}";
            GatherBuddy.Log.Error($"[BaitGuard] {LastStatus}");
        }
    }
}
