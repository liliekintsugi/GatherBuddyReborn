using System.Collections.Generic;
using System.Linq;
using GatherBuddy.AutoHookIntegration;
using GatherBuddy.Classes;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather.OceanFishing;

// Builds and caches AutoHook presets keyed by (routeId, segment order, spectral flag).
//
// PR 2 scope: build-on-demand + apply. A future PR will pre-build all presets up front,
// expose priority configuration, and listen to SpectralDetector.SpectralChanged to switch
// automatically. For now this is the manual building block.
public sealed class OceanPresetCache
{
    private readonly Dictionary<(byte RouteId, int Order, bool Spectral), string> _cache = new();

    public string? GetOrBuild(OceanRoute route, int segmentOrder, bool spectral)
    {
        var key = (route.Id, segmentOrder, spectral);
        if (_cache.TryGetValue(key, out var existing))
            return existing;

        var spot = route.GetSpot(segmentOrder, spectral);
        if (spot == null)
        {
            GatherBuddy.Log.Warning($"[OceanFishing/Presets] No fishing spot for route={route.Id} order={segmentOrder} spectral={spectral}.");
            return null;
        }

        var fishList = ResolveFishForSpot(spot).ToList();
        if (fishList.Count == 0)
        {
            GatherBuddy.Log.Warning($"[OceanFishing/Presets] No fish found for spot '{spot.Name}'.");
            return null;
        }

        var name = $"GBR_Ocean_{route.Id}_{segmentOrder}_{(spectral ? "S" : "N")}";

        if (!AutoHookService.ExportPresetToAutoHook(name, fishList, gbrPreset: null, selectPreset: false))
        {
            GatherBuddy.Log.Error($"[OceanFishing/Presets] Failed to export preset '{name}' to AutoHook.");
            return null;
        }

        _cache[key] = name;
        GatherBuddy.Log.Information($"[OceanFishing/Presets] Built preset '{name}' for spot '{spot.Name}' with {fishList.Count} fish.");
        return name;
    }

    // Apply (select) the preset for the given segment. Builds it on first use.
    public bool Apply(OceanRoute route, int segmentOrder, bool spectral)
    {
        var name = GetOrBuild(route, segmentOrder, spectral);
        if (name == null)
            return false;

        if (!AutoHookService.IsAutoHookAvailable())
        {
            GatherBuddy.Log.Error("[OceanFishing/Presets] AutoHook not available; cannot apply preset.");
            return false;
        }

        AutoHook.SetPreset?.Invoke(name);
        GatherBuddy.Log.Information($"[OceanFishing/Presets] Applied preset '{name}'.");
        return true;
    }

    public void Clear()
    {
        _cache.Clear();
        GatherBuddy.Log.Information("[OceanFishing/Presets] Cache cleared.");
    }

    public int Count => _cache.Count;

    private static IEnumerable<Fish> ResolveFishForSpot(FishingSpot spot)
        => GatherBuddy.GameData.Fishes.Values
            .Where(f => f.FishingSpots.Any(s => s.Id == spot.Id))
            .OrderBy(f => f.ItemId);
}
