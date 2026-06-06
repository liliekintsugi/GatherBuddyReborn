using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;

namespace GatherBuddy.AutoGather.OceanFishing;

// Detects whether the current ocean-fishing trip is in a Spectral Current.
//
// Approach: ocean fishing's spectral mode is encoded as the territory's "individual weather" flipping
// to a row whose name contains "Spectral" in the Weather sheet. EnhancedCurrentWeather already reads
// the per-territory weather via WeatherManager; we just classify the id and fire a delta event.
public sealed class SpectralDetector : IDisposable
{
    public const ushort OceanFishingTerritoryId = OceanUptime.OceanFishingTerritoryId;

    private readonly HashSet<byte> _spectralWeatherIds;
    private bool                   _wasSpectral;

    public bool IsSpectralActive { get; private set; }
    public byte LastWeatherId    { get; private set; }

    // Fires only on a transition. true = entered spectral, false = exited spectral.
    public event Action<bool>? SpectralChanged;

    public SpectralDetector()
    {
        _spectralWeatherIds = GatherBuddy.GameData.Weathers.Values
            .Where(w => w.Name?.IndexOf("Spectral", StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(w => (byte)w.Id)
            .ToHashSet();

        if (_spectralWeatherIds.Count == 0)
            GatherBuddy.Log.Warning("[OceanFishing/Spectral] No weather rows matched 'Spectral'; detector will never fire.");
        else
            GatherBuddy.Log.Information($"[OceanFishing/Spectral] Tracking {_spectralWeatherIds.Count} spectral weather id(s).");
    }

    // Call once per framework tick (cheap). Caller decides whether to skip outside territory 900.
    public void Tick()
    {
        if (Dalamud.ClientState.TerritoryType != OceanFishingTerritoryId)
        {
            if (IsSpectralActive)
            {
                IsSpectralActive = false;
                _wasSpectral     = false;
                LastWeatherId    = 0;
                SpectralChanged?.Invoke(false);
            }
            return;
        }

        var id = EnhancedCurrentWeather.GetCurrentWeatherId();
        LastWeatherId   = id;
        IsSpectralActive = _spectralWeatherIds.Contains(id);

        if (IsSpectralActive == _wasSpectral)
            return;

        _wasSpectral = IsSpectralActive;
        GatherBuddy.Log.Debug($"[OceanFishing/Spectral] Transition → {(IsSpectralActive ? "ENTERED" : "EXITED")} (weatherId={id}).");
        SpectralChanged?.Invoke(IsSpectralActive);
    }

    public IReadOnlyCollection<byte> SpectralWeatherIds => _spectralWeatherIds;

    public void Dispose()
    {
        SpectralChanged = null;
    }
}
