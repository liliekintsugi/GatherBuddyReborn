using System;
using System.Linq;
using GatherBuddy.Classes;
using GatherBuddy.Plugin;

namespace GatherBuddy;

// Lightweight handler for /gbocean, split out from GatherBuddy.Commands.cs to keep ocean-fishing
// surface area contained while PR 2 stabilizes.
public partial class GatherBuddy
{
    private void OnGbOcean(string command, string arguments)
    {
        var parts = (arguments ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            Communicator.Print("Usage: /gbocean preset [aldenard|othard] [0|1|2] [normal|spectral]");
            Communicator.Print("       /gbocean clear     — empty the preset cache");
            Communicator.Print("       /gbocean status    — print spectral detector state");
            return;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "clear":
                OceanPresetCache.Clear();
                Communicator.Print("[Ocean] Preset cache cleared.");
                return;

            case "status":
                Communicator.Print($"[Ocean] Territory={Dalamud.ClientState.TerritoryType} "
                                 + $"weatherId={SpectralDetector.LastWeatherId} "
                                 + $"spectral={SpectralDetector.IsSpectralActive} "
                                 + $"cachedPresets={OceanPresetCache.Count}");
                return;

            case "preset":
                HandlePresetCommand(parts);
                return;

            default:
                Communicator.Print($"[Ocean] Unknown subcommand '{parts[0]}'. Use /gbocean for help.");
                return;
        }
    }

    private void HandlePresetCommand(string[] parts)
    {
        var area = OceanArea.Aldenard;
        var order = 0;
        var spectral = SpectralDetector.IsSpectralActive;

        for (var i = 1; i < parts.Length; ++i)
        {
            var p = parts[i].ToLowerInvariant();
            switch (p)
            {
                case "aldenard": area = OceanArea.Aldenard; break;
                case "othard":   area = OceanArea.Othard;   break;
                case "normal":   spectral = false; break;
                case "spectral": spectral = true;  break;
                default:
                    if (int.TryParse(p, out var n) && n is >= 0 and <= 2)
                        order = n;
                    else
                        Communicator.Print($"[Ocean] Ignoring unknown argument '{p}'.");
                    break;
            }
        }

        try
        {
            var route = Plugin.OceanUptime.NextOceanRoute(area, Time.ServerTime);
            var spot  = route.GetSpot(order, spectral);
            Communicator.Print($"[Ocean] Applying preset for {area} route '{route.Name}' segment {order} ({(spectral ? "spectral" : "normal")}) — spot '{spot.Name}'.");
            var ok = OceanPresetCache.Apply(route, order, spectral);
            Communicator.Print(ok
                ? "[Ocean] Preset applied via AutoHook."
                : "[Ocean] Failed to apply preset; see /xllog for details.");
        }
        catch (Exception e)
        {
            Communicator.Print($"[Ocean] Error: {e.Message}");
            Log.Error($"[Ocean] preset command failed: {e}");
        }
    }
}
