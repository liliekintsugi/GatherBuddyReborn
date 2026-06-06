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
