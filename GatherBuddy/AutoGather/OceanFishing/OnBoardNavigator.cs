using System;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather.OceanFishing;

// PR 9b — once the trip starts (territory 900), nothing in the existing pipeline gets the
// player to a fishing hole on the boat or starts casting. This navigator fills that gap:
//   1. On trip entry, wait a settling delay (the boat is still finalizing animations / cutscene).
//   2. Switch to the Fisher gear set (if Config.UseGearChange) so the rod is equipped.
//   3. vnavmesh-walk to OnBoardFishingSpot (user-configurable position on the boat).
//   4. Trigger the FSH "Cast" action (id 289). After that, AutoHook's AutoStartFishing keeps the
//      loop going across catches.
// Everything is gated by OnBoardNavigatorEnabled — when off, the user keeps doing it manually.
public sealed class OnBoardNavigator : IDisposable
{
    private enum NavState { Idle, Settling, Equipping, Pathing, Moving, Casting, Done }

    private static Config.OceanFishingConfig Cfg => GatherBuddy.Config.OceanFishing;
    private static void Save() => GatherBuddy.Config.Save();

    public bool Enabled
    {
        get => Cfg.OnBoardNavigatorEnabled;
        set { if (Cfg.OnBoardNavigatorEnabled == value) return; Cfg.OnBoardNavigatorEnabled = value; Save(); }
    }

    public Vector3 FishingSpot
    {
        get => Cfg.OnBoardFishingSpot;
        set { if (Cfg.OnBoardFishingSpot == value) return; Cfg.OnBoardFishingSpot = value; Save(); }
    }

    public int SettleDelaySeconds
    {
        get => Cfg.OnBoardSettleDelaySeconds;
        set { if (Cfg.OnBoardSettleDelaySeconds == value) return; Cfg.OnBoardSettleDelaySeconds = value; Save(); }
    }

    public bool AutoCast
    {
        get => Cfg.OnBoardAutoCast;
        set { if (Cfg.OnBoardAutoCast == value) return; Cfg.OnBoardAutoCast = value; Save(); }
    }

    public string LastStatus { get; private set; } = string.Empty;

    private NavState _state = NavState.Idle;
    private DateTime _stateEnteredAt = DateTime.UtcNow;
    private System.Threading.Tasks.Task<System.Collections.Generic.List<Vector3>>? _pathTask;

    public void Tick()
    {
        if (!Enabled) return;

        var onBoat = Dalamud.ClientState.TerritoryType == SpectralDetector.OceanFishingTerritoryId;
        if (!onBoat)
        {
            if (_state != NavState.Idle)
                Reset();
            return;
        }

        // First tick on the boat — start the sequence.
        if (_state == NavState.Idle)
            Transition(NavState.Settling);

        try
        {
            switch (_state)
            {
                case NavState.Settling: TickSettling(); break;
                case NavState.Equipping: TickEquipping(); break;
                case NavState.Pathing:   TickPathing();   break;
                case NavState.Moving:    TickMoving();    break;
                case NavState.Casting:   TickCasting();   break;
                case NavState.Done:      break;
            }
        }
        catch (Exception e)
        {
            LastStatus = $"failed in {_state}: {e.Message}";
            GatherBuddy.Log.Error($"[OnBoard] {_state} failed: {e}");
            Transition(NavState.Done);
        }
    }

    private void TickSettling()
    {
        if ((DateTime.UtcNow - _stateEnteredAt).TotalSeconds < SettleDelaySeconds)
            return;
        Transition(NavState.Equipping);
    }

    private void TickEquipping()
    {
        if (GatherBuddy.Config.UseGearChange && !string.IsNullOrWhiteSpace(GatherBuddy.Config.FisherSetName))
        {
            var player = Dalamud.Objects.LocalPlayer;
            var isFisher = player?.ClassJob.RowId == 18;
            if (!isFisher)
            {
                Chat.ExecuteCommand($"/gearset change \"{GatherBuddy.Config.FisherSetName}\"");
                LastStatus = $"Switching to '{GatherBuddy.Config.FisherSetName}'…";
                // Brief grace period for the gear change.
                if ((DateTime.UtcNow - _stateEnteredAt).TotalSeconds < 2)
                    return;
            }
        }
        Transition(NavState.Pathing);
    }

    private void TickPathing()
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null) return;

        if (FishingSpot == Vector3.Zero)
        {
            LastStatus = "OnBoardFishingSpot not configured. Stand at a rail, click 'Capture current position' in the UI.";
            Transition(NavState.Done);
            return;
        }
        if (!VNavmesh.Enabled)
        {
            LastStatus = "vnavmesh not loaded";
            Transition(NavState.Done);
            return;
        }

        var dest = VNavmesh.Query.Mesh.NearestPoint?.Invoke(FishingSpot, 10f, 10f) ?? FishingSpot;
        _pathTask = VNavmesh.Nav.Pathfind(player.Position, dest, false);
        Transition(NavState.Moving);
    }

    private void TickMoving()
    {
        if (_pathTask is null) { Transition(NavState.Casting); return; }
        if (!_pathTask.IsCompleted) return;
        if (_pathTask.IsFaulted)
        {
            LastStatus = $"pathfind faulted: {_pathTask.Exception?.GetBaseException().Message}";
            Transition(NavState.Casting); // Try to cast anyway if we're close.
            return;
        }
        var waypoints = _pathTask.Result;
        if (waypoints == null || waypoints.Count == 0)
        {
            LastStatus = "pathfind empty — already at spot or destination off-mesh.";
            Transition(NavState.Casting);
            return;
        }
        VNavmesh.Path.MoveTo(waypoints, false);

        // Wait until vnavmesh reports done OR we're within 2m.
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null) return;
        var dist = Vector3.Distance(player.Position, FishingSpot);
        var moving = VNavmesh.Path.IsRunning();
        if (!moving || dist < 2f)
        {
            try { VNavmesh.Path.Stop(); } catch { /* ignore */ }
            Transition(NavState.Casting);
        }
    }

    private unsafe void TickCasting()
    {
        if (!AutoCast)
        {
            LastStatus = "Arrived. Auto-cast disabled — cast manually.";
            Transition(NavState.Done);
            return;
        }

        // Wait a beat so the gear-change / movement actually settle.
        if ((DateTime.UtcNow - _stateEnteredAt).TotalMilliseconds < 800)
            return;

        // FSH 'Cast' = action id 289. Only fires if rod is equipped and we're at a fishing hole.
        var status = ActionManager.Instance()->GetActionStatus(ActionType.Action, 289);
        if (status != 0)
        {
            LastStatus = $"Cast action not usable (status={status}). Will retry in a moment.";
            // Stay in Casting; if we never become ready, give up after 8s.
            if ((DateTime.UtcNow - _stateEnteredAt).TotalSeconds > 8)
                Transition(NavState.Done);
            return;
        }

        var ok = ActionManager.Instance()->UseAction(ActionType.Action, 289);
        LastStatus = ok ? "Cast triggered — AutoHook should take over." : "UseAction(Cast) returned false.";
        GatherBuddy.Log.Information($"[OnBoard] Cast → {ok}");
        Transition(NavState.Done);
    }

    private void Transition(NavState s)
    {
        if (_state == s) return;
        GatherBuddy.Log.Debug($"[OnBoard] {_state} → {s}");
        _state = s;
        _stateEnteredAt = DateTime.UtcNow;
    }

    private void Reset()
    {
        _state = NavState.Idle;
        _stateEnteredAt = DateTime.UtcNow;
        _pathTask = null;
        LastStatus = string.Empty;
    }

    public void Dispose() { }

    // For the UI button: stamp current position into config.
    public void CaptureCurrentPosition()
    {
        var p = Dalamud.Objects.LocalPlayer;
        if (p == null) return;
        FishingSpot = p.Position;
        LastStatus = $"Captured {p.Position}";
    }
}
