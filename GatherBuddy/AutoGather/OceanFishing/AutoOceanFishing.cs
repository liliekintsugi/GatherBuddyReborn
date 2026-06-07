using System;
using GatherBuddy.Classes;
using GatherBuddy.FishTimer.Parser;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather.OceanFishing;

// PR 3 surface: trip-only auto-switch.
//
// Assumes the player is already on the ocean fishing boat (territory 900). Tracks the active
// OceanRoute (Aldenard by default), watches SpectralDetector + FishingParser.BeganFishing, and
// re-applies the matching AutoHook preset on every transition.
//
// No embarkation, no NPC interaction, no movement — that lands in PR 4.
public sealed class AutoOceanFishing : IDisposable
{
    private static Config.OceanFishingConfig Cfg => GatherBuddy.Config.OceanFishing;
    private static void Save() => GatherBuddy.Config.Save();

    public bool Enabled
    {
        get => Cfg.AutoOceanEnabled;
        set { if (Cfg.AutoOceanEnabled == value) return; Cfg.AutoOceanEnabled = value; Save(); }
    }

    public OceanArea PreferredArea
    {
        get => (OceanArea)Cfg.PreferredOceanArea;
        set { var b = (byte)value; if (Cfg.PreferredOceanArea == b) return; Cfg.PreferredOceanArea = b; Save(); }
    }

    public OceanRoute? CurrentRoute    { get; private set; }
    public int         CurrentSegment  { get; private set; } = -1;
    public bool        CurrentSpectral { get; private set; } = false;
    public string?     LastAppliedPreset { get; private set; }

    // PR 7c — auto-toggle AutoHook on the boat so the player doesn't have to enable it manually.
    // We save the prior state on trip enter and restore it on trip exit so we don't surprise the
    // user when they leave.
    public bool ManageAutoHookState
    {
        get => Cfg.AutoOceanManageAutoHookState;
        set { if (Cfg.AutoOceanManageAutoHookState == value) return; Cfg.AutoOceanManageAutoHookState = value; Save(); }
    }
    private bool? _savedPluginState;
    private bool? _savedAutoStart;

    private readonly SpectralDetector _spectral;
    private readonly OceanPresetCache _presets;
    private readonly FishingParser    _parser;
    private bool                      _wasInTerritory;

    public AutoOceanFishing(SpectralDetector spectral, OceanPresetCache presets, FishingParser parser)
    {
        _spectral = spectral;
        _presets  = presets;
        _parser   = parser;

        _spectral.SpectralChanged += OnSpectralChanged;
        _parser.BeganFishing      += OnBeganFishing;
    }

    public void Tick()
    {
        if (!Enabled)
            return;

        var inTerritory = Dalamud.ClientState.TerritoryType == SpectralDetector.OceanFishingTerritoryId;
        if (inTerritory == _wasInTerritory)
            return;

        _wasInTerritory = inTerritory;
        if (inTerritory)
            OnEnterTrip();
        else
            OnLeaveTrip();
    }

    private void OnEnterTrip()
    {
        try
        {
            CurrentRoute    = OceanUptime.NextOceanRoute(PreferredArea, GatherBuddy.Time.ServerTime);
            CurrentSegment  = 0;
            CurrentSpectral = _spectral.IsSpectralActive;
            GatherBuddy.Log.Information(
                $"[AutoOcean] Entered trip — route '{CurrentRoute.Name}', starting segment 0, spectral={CurrentSpectral}.");
            Apply();
            EnableAutoHook();
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"[AutoOcean] Failed to resolve current route on trip entry: {e}");
            CurrentRoute = null;
        }
    }

    private void OnLeaveTrip()
    {
        GatherBuddy.Log.Information("[AutoOcean] Trip ended — clearing state.");
        CurrentRoute      = null;
        CurrentSegment    = -1;
        CurrentSpectral   = false;
        LastAppliedPreset = null;
        RestoreAutoHook();
    }

    private void EnableAutoHook()
    {
        if (!ManageAutoHookState)
            return;
        if (!AutoHook.Enabled)
        {
            GatherBuddy.Log.Warning("[AutoOcean] AutoHook plugin not available; cannot auto-enable.");
            return;
        }

        try
        {
            _savedPluginState = AutoHook.GetPluginState?.Invoke();
            _savedAutoStart   = AutoHook.GetAutoStartFishing?.Invoke();

            AutoHook.SetPluginState?.Invoke(true);
            AutoHook.SetAutoStartFishing?.Invoke(true);
            GatherBuddy.Log.Information(
                $"[AutoOcean] AutoHook enabled (saved prior plugin={_savedPluginState}, autoStart={_savedAutoStart}).");
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"[AutoOcean] Failed to toggle AutoHook on: {e}");
        }
    }

    private void RestoreAutoHook()
    {
        if (!ManageAutoHookState)
            return;
        if (_savedPluginState is null && _savedAutoStart is null)
            return;
        if (!AutoHook.Enabled)
        {
            _savedPluginState = null;
            _savedAutoStart   = null;
            return;
        }

        try
        {
            if (_savedPluginState.HasValue) AutoHook.SetPluginState?.Invoke(_savedPluginState.Value);
            if (_savedAutoStart.HasValue)   AutoHook.SetAutoStartFishing?.Invoke(_savedAutoStart.Value);
            GatherBuddy.Log.Information(
                $"[AutoOcean] AutoHook restored (plugin={_savedPluginState}, autoStart={_savedAutoStart}).");
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"[AutoOcean] Failed to restore AutoHook: {e}");
        }
        finally
        {
            _savedPluginState = null;
            _savedAutoStart   = null;
        }
    }

    private void OnSpectralChanged(bool spectral)
    {
        if (!Enabled || CurrentRoute == null)
            return;

        if (CurrentSpectral == spectral)
            return;

        CurrentSpectral = spectral;
        GatherBuddy.Log.Information($"[AutoOcean] Spectral transition → {spectral}, reapplying preset.");
        Apply();
    }

    // Cast events let us refine the segment index: the FishingSpot identifies the segment uniquely
    // within an OceanRoute. We use it both as a trigger ("the boat just moved to a new zone") and
    // as a correction if the time-based initial guess was off.
    private void OnBeganFishing(FishingSpot? spot)
    {
        if (!Enabled || CurrentRoute == null || spot == null)
            return;

        // Try to find this spot in the current route across all (order, spectral) combinations.
        for (var order = 0; order < 3; ++order)
        {
            for (var spectralIdx = 0; spectralIdx < 2; ++spectralIdx)
            {
                var spectral = spectralIdx == 1;
                var routeSpot = CurrentRoute.GetSpot(order, spectral);
                if (routeSpot != null && routeSpot.Id == spot.Id)
                {
                    if (order != CurrentSegment || spectral != CurrentSpectral)
                    {
                        GatherBuddy.Log.Information(
                            $"[AutoOcean] Cast spot '{spot.Name}' → segment {order}, spectral={spectral} (was {CurrentSegment}/{CurrentSpectral}).");
                        CurrentSegment  = order;
                        CurrentSpectral = spectral;
                        Apply();
                    }
                    return;
                }
            }
        }

        GatherBuddy.Log.Verbose($"[AutoOcean] Cast at spot '{spot.Name}' is not on tracked route '{CurrentRoute.Name}'.");
    }

    private void Apply()
    {
        if (CurrentRoute == null || CurrentSegment < 0)
            return;

        var name = _presets.GetOrBuild(CurrentRoute, CurrentSegment, CurrentSpectral);
        if (name == null)
            return;

        if (name == LastAppliedPreset)
            return;

        if (_presets.Apply(CurrentRoute, CurrentSegment, CurrentSpectral))
            LastAppliedPreset = name;
    }

    public void Dispose()
    {
        if (_parser != null)
            _parser.BeganFishing -= OnBeganFishing;
        if (_spectral != null)
            _spectral.SpectralChanged -= OnSpectralChanged;
    }
}
