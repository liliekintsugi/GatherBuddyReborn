using System.Linq;
using Dalamud.Bindings.ImGui;
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
}
