using System.Linq;
using Dalamud.Bindings.ImGui;
using System.Collections.Generic;
using GatherBuddy.AutoGather.OceanFishing;
using GatherBuddy.Classes;
using GatherBuddy.Plugin;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    // PR 1 surface: read-only debug for the ocean-fishing spectral detector + current route.
    // Will be replaced by a full Ocean Fishing config tab in later PRs.
    private void DrawOceanFishingTab()
    {
        using var id  = ImRaii.PushId("OceanFishing");
        using var tab = ImRaii.TabItem("Ocean Fishing");
        if (!tab)
            return;

        var territory = Dalamud.ClientState.TerritoryType;
        var inOcean   = territory == SpectralDetector.OceanFishingTerritoryId;
        var detector  = GatherBuddy.SpectralDetector;

        ImGui.TextUnformatted($"Territory: {territory}  ({(inOcean ? "ocean fishing" : "elsewhere")})");
        ImGui.Separator();

        if (detector == null)
        {
            ImGui.TextDisabled("SpectralDetector not initialized.");
            return;
        }

        ImGui.TextUnformatted($"Spectral weather ids tracked: {detector.SpectralWeatherIds.Count}");
        if (detector.SpectralWeatherIds.Count > 0)
        {
            var preview = string.Join(", ", detector.SpectralWeatherIds.Take(16));
            ImGui.TextDisabled(preview);
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Current weather id: {detector.LastWeatherId}");
        ImGui.TextUnformatted("Spectral active: ");
        ImGui.SameLine();
        if (detector.IsSpectralActive)
            ImGui.TextColored(new System.Numerics.Vector4(0.4f, 1f, 0.6f, 1f), "YES");
        else
            ImGui.TextDisabled("no");

        ImGui.Separator();

        // AutoOceanFishing controls (PR 3).
        var auto = GatherBuddy.AutoOceanFishing;
        if (auto != null)
        {
            var enabled = auto.Enabled;
            if (ImGui.Checkbox("Enable AutoOceanFishing (preset switch on segment / spectral)", ref enabled))
                auto.Enabled = enabled;

            var areaIdx = auto.PreferredArea == OceanArea.Othard ? 1 : 0;
            string[] areas = ["Aldenard", "Othard"];
            if (ImGui.Combo("Preferred area", ref areaIdx, areas, areas.Length))
                auto.PreferredArea = areaIdx == 1 ? OceanArea.Othard : OceanArea.Aldenard;

            ImGui.TextUnformatted($"Current route   : {auto.CurrentRoute?.Name ?? "<none>"}");
            ImGui.TextUnformatted($"Current segment : {auto.CurrentSegment} (spectral={auto.CurrentSpectral})");
            ImGui.TextUnformatted($"Last applied    : {auto.LastAppliedPreset ?? "<none>"}");
            ImGui.Separator();
        }

        // EmbarkController controls (PR 4).
        var embark = GatherBuddy.EmbarkController;
        if (embark != null)
        {
            var embEnabled = embark.Enabled;
            if (ImGui.Checkbox("Enable AutoEmbark (path to ferry NPC + interact)", ref embEnabled))
                embark.Enabled = embEnabled;

            ImGui.TextUnformatted($"Embark state : {embark.State}");
            if (!string.IsNullOrEmpty(embark.LastError))
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.5f, 0.4f, 1f), $"Last error: {embark.LastError}");

            var pos = embark.FerryStandPosition;
            if (ImGui.InputFloat3("Ferry stand position", ref pos))
                embark.FerryStandPosition = pos;

            var npcId = (int)embark.FerrySkipperDataId;
            if (ImGui.InputInt("Ferry Skipper data id", ref npcId))
                embark.FerrySkipperDataId = (uint)System.Math.Max(0, npcId);

            var ferryTerritory = (int)embark.FerryTerritoryId;
            if (ImGui.InputInt("Ferry territory id", ref ferryTerritory))
                embark.FerryTerritoryId = (ushort)System.Math.Clamp(ferryTerritory, 0, ushort.MaxValue);

            var sel = embark.SelectStringBoardIndex;
            if (ImGui.InputInt("SelectString board index", ref sel))
                embark.SelectStringBoardIndex = System.Math.Max(0, sel);

            ImGui.Separator();

            // BaitRestock controls (PR 6).
            var restock = embark.Restock;
            var rEnabled = restock.Enabled;
            if (ImGui.Checkbox("Enable bait auto-restock before embark", ref rEnabled))
                restock.Enabled = rEnabled;

            var listIdStr = restock.BuyListId?.ToString() ?? string.Empty;
            if (ImGui.InputText("Vendor buy list id (Guid)", ref listIdStr, 64))
            {
                if (System.Guid.TryParse(listIdStr, out var g))
                    restock.BuyListId = g;
                else if (string.IsNullOrWhiteSpace(listIdStr))
                    restock.BuyListId = null;
            }

            ImGui.TextDisabled("Build the bait list in Vulcan → Vendors, copy its id here.");
            if (!string.IsNullOrEmpty(restock.LastStatus))
                ImGui.TextUnformatted($"Last: {restock.LastStatus}");
            if (restock.IsRunning)
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.9f, 0.4f, 1f), $"Restock running: {restock.RunnerStatus}");

            ImGui.TextUnformatted("Inventory snapshot:");
            foreach (var (t, have) in restock.Snapshot())
            {
                var color = have < t.LowThreshold
                    ? new System.Numerics.Vector4(1f, 0.5f, 0.4f, 1f)
                    : new System.Numerics.Vector4(0.6f, 1f, 0.6f, 1f);
                ImGui.TextColored(color, $"  {t.Name,-16} {have} / {t.RestockTarget}  (low<{t.LowThreshold})");
            }

            ImGui.Separator();
        }

        // Bait Advisor (PR 7a) — global across all AutoGather fish lists.
        DrawBaitAdvisorSection();

        // Bait Guard (PR 7b) — auto-queue + auto-start vendor list on threshold.
        DrawBaitGuardSection();

        // Show the next ocean route per area, regardless of territory, as a debug helper.
        var now = GatherBuddy.Time.ServerTime;
        try
        {
            var aldenard = OceanUptime.NextOceanRoute(OceanArea.Aldenard, now);
            var othard   = OceanUptime.NextOceanRoute(OceanArea.Othard,   now);
            ImGui.TextUnformatted($"Next route (Aldenard): {aldenard.Name}  [start: {aldenard.StartTime}]");
            ImGui.TextUnformatted($"Next route (Othard):   {othard.Name}  [start: {othard.StartTime}]");

            ImGui.Separator();
            ImGui.TextUnformatted($"Cached presets: {GatherBuddy.OceanPresetCache.Count}");
            if (ImGui.Button("Apply Aldenard seg 0 (auto spectral)"))
                GatherBuddy.OceanPresetCache.Apply(aldenard, 0, detector.IsSpectralActive);
            ImGui.SameLine();
            if (ImGui.Button("Apply Othard seg 0 (auto spectral)"))
                GatherBuddy.OceanPresetCache.Apply(othard, 0, detector.IsSpectralActive);
            if (ImGui.Button("Clear cache"))
                GatherBuddy.OceanPresetCache.Clear();
        }
        catch (System.Exception e)
        {
            ImGui.TextDisabled($"Route lookup failed: {e.Message}");
        }
    }

    private static void DrawBaitGuardSection()
    {
        var guard = GatherBuddy.BaitGuard;
        if (guard == null) return;
        if (!ImGui.CollapsingHeader("Bait Guard (auto-restock during AutoGather)"))
            return;

        var enabled = guard.Enabled;
        if (ImGui.Checkbox("Enable", ref enabled))
            guard.Enabled = enabled;

        var only = guard.OnlyWhenAutoGatherEnabled;
        if (ImGui.Checkbox("Only when AutoGather is running", ref only))
            guard.OnlyWhenAutoGatherEnabled = only;

        var frac = guard.TriggerBelowFraction;
        if (ImGui.SliderFloat("Trigger below (fraction of desired)", ref frac, 0.05f, 1f, "%.2f"))
            guard.TriggerBelowFraction = System.Math.Clamp(frac, 0.05f, 1f);

        var qty = guard.DesiredQtyPerFish;
        if (ImGui.InputInt("Desired qty per fish", ref qty))
            guard.DesiredQtyPerFish = System.Math.Clamp(qty, 1, 999);

        var interval = (int)guard.CheckInterval.TotalSeconds;
        if (ImGui.SliderInt("Check interval (s)", ref interval, 5, 120))
            guard.CheckInterval = System.TimeSpan.FromSeconds(interval);

        ImGui.TextUnformatted($"Last check : {(guard.LastCheckUtc == System.DateTime.MinValue ? "<never>" : guard.LastCheckUtc.ToLocalTime().ToString("HH:mm:ss"))}");
        ImGui.TextUnformatted($"Last queued: {guard.LastQueuedCount}");
        ImGui.TextUnformatted($"Total queued: {guard.TotalQueuedCount}");
        if (!string.IsNullOrEmpty(guard.LastStatus))
            ImGui.TextDisabled(guard.LastStatus);
    }

    private static int _baitAdvisorDesiredPerFish = BaitAdvisor.DefaultDesiredQtyPerFish;
    private static List<BaitAdvisor.MissingBait>? _baitAdvisorSnapshot;

    private static void DrawBaitAdvisorSection()
    {
        if (ImGui.CollapsingHeader("Bait Advisor (all AutoGather fish lists)"))
        {
            ImGui.TextDisabled("Reads your active + fallback AutoGather lists, resolves each fish's");
            ImGui.TextDisabled("recommended bait, and surfaces what's missing. 'Add to buy list' queues");
            ImGui.TextDisabled("the bait via the existing Vendor buy-list automation.");

            if (ImGui.InputInt("Desired qty per fish", ref _baitAdvisorDesiredPerFish))
                _baitAdvisorDesiredPerFish = System.Math.Clamp(_baitAdvisorDesiredPerFish, 1, 999);

            if (ImGui.Button("Refresh"))
                _baitAdvisorSnapshot = BaitAdvisor.Scan(_baitAdvisorDesiredPerFish);
            ImGui.SameLine();
            if (ImGui.Button("Queue ALL missing"))
            {
                _baitAdvisorSnapshot ??= BaitAdvisor.Scan(_baitAdvisorDesiredPerFish);
                var n = BaitAdvisor.QueueAllMissing(_baitAdvisorSnapshot);
                Plugin.Communicator.Print($"[BaitAdvisor] Queued {n} bait(s) to the active vendor buy list.");
            }

            var snap = _baitAdvisorSnapshot;
            if (snap == null || snap.Count == 0)
            {
                ImGui.TextDisabled("Click Refresh to scan.");
                return;
            }

            using var table = ElliLib.Raii.ImRaii.Table("##BaitAdvisorTable", 5,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders);
            if (!table) return;
            ImGui.TableSetupColumn("Bait");
            ImGui.TableSetupColumn("Have");
            ImGui.TableSetupColumn("Desired");
            ImGui.TableSetupColumn("# Fish");
            ImGui.TableSetupColumn("Action");
            ImGui.TableHeadersRow();

            foreach (var b in snap)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var color = b.HaveQty < b.DesiredQty
                    ? new System.Numerics.Vector4(1f, 0.55f, 0.4f, 1f)
                    : new System.Numerics.Vector4(0.6f, 1f, 0.6f, 1f);
                ImGui.TextColored(color, b.BaitName);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(b.HaveQty.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(b.DesiredQty.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(b.FishNames.Count.ToString());
                if (ImGui.IsItemHovered() && b.FishNames.Count > 0)
                    ImGui.SetTooltip(string.Join("\n", b.FishNames));
                ImGui.TableNextColumn();
                using (ElliLib.Raii.ImRaii.Disabled(b.HaveQty >= b.DesiredQty))
                {
                    if (ImGui.Button($"Add##{b.BaitItemId}"))
                        BaitAdvisor.QueueRestock(b);
                }
            }
        }
    }
}
