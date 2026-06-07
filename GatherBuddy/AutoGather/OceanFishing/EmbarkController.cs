using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather.OceanFishing;

// PR 4 — Autonomous embarkation.
//
// State machine driven from Tick() (framework update). Stays Disabled unless the user explicitly
// flips Enabled and is outside territory 900; the moment we're on the boat, control is handed back
// to AutoOceanFishing.
//
// Defaults target the Limsa Lominsa Lower Decks "Ferry Skipper" but every coordinate / id is
// overridable from the UI so the user can recover from data drift without a rebuild.
public sealed class EmbarkController : IDisposable
{
    private static Config.OceanFishingConfig Cfg => GatherBuddy.Config.OceanFishing;
    private static void Save() => GatherBuddy.Config.Save();

    public bool Enabled
    {
        get => Cfg.EmbarkEnabled;
        set { if (Cfg.EmbarkEnabled == value) return; Cfg.EmbarkEnabled = value; Save(); }
    }

    public ushort FerryTerritoryId
    {
        get => Cfg.FerryTerritoryId;
        set { if (Cfg.FerryTerritoryId == value) return; Cfg.FerryTerritoryId = value; Save(); }
    }

    public Vector3 FerryStandPosition
    {
        get => Cfg.FerryStandPosition;
        set { if (Cfg.FerryStandPosition == value) return; Cfg.FerryStandPosition = value; Save(); }
    }

    public uint FerrySkipperDataId
    {
        get => Cfg.FerrySkipperDataId;
        set { if (Cfg.FerrySkipperDataId == value) return; Cfg.FerrySkipperDataId = value; Save(); }
    }

    public int SelectStringBoardIndex
    {
        get => Cfg.SelectStringBoardIndex;
        set { if (Cfg.SelectStringBoardIndex == value) return; Cfg.SelectStringBoardIndex = value; Save(); }
    }

    public BaitRestock Restock { get; } = new();

    public int LeadTimeMinutes
    {
        get => Cfg.LeadTimeMinutes;
        set { if (Cfg.LeadTimeMinutes == value) return; Cfg.LeadTimeMinutes = value; Save(); }
    }

    // Public read for the UI / debug overlay.
    public long MsUntilNextDeparture
        => global::GatherBuddy.Plugin.OceanUptime.MillisecondsUntilNextDeparture(GatherBuddy.Time.ServerTime);

    public EmbarkState State { get; private set; } = EmbarkState.Idle;
    public string?     LastError { get; private set; }

    private DateTime _stateEnteredAt = DateTime.UtcNow;
    private DateTime _idleRetryAfter = DateTime.MinValue;
    private string?  _lastLoggedRestockStatus;
    private Task<System.Collections.Generic.List<Vector3>>? _pathTask;

    public void Tick()
    {
        if (!Enabled)
        {
            if (State != EmbarkState.Idle && State != EmbarkState.Boarded)
                Transition(EmbarkState.Idle);
            return;
        }

        // On boat → embark complete, hand off.
        if (Dalamud.ClientState.TerritoryType == SpectralDetector.OceanFishingTerritoryId)
        {
            if (State != EmbarkState.Boarded)
                Transition(EmbarkState.Boarded);
            return;
        }

        try
        {
            switch (State)
            {
                case EmbarkState.Idle:           TickIdle();           break;
                case EmbarkState.Restocking:     TickRestocking();     break;
                case EmbarkState.Pathing:        TickPathing();        break;
                case EmbarkState.Moving:         TickMoving();         break;
                case EmbarkState.AtNpc:          TickAtNpc();          break;
                case EmbarkState.Interacting:    TickInteracting();    break;
                case EmbarkState.SelectingRoute: TickSelectingRoute(); break;
                case EmbarkState.Boarded:        /* handled above */   break;
            }
        }
        catch (Exception e)
        {
            LastError = e.Message;
            GatherBuddy.Log.Error($"[Embark] {State} failed: {e}");
            Transition(EmbarkState.Idle);
        }
    }

    private void TickIdle()
    {
        // Cooldown after any failure so we don't spam pathfind / restock at framerate.
        if (DateTime.UtcNow < _idleRetryAfter)
            return;

        if (Dalamud.ClientState.TerritoryType != FerryTerritoryId)
            return;

        // Time-gate: wait until next departure is inside LeadTimeMinutes. Keeps us from walking
        // to the NPC 90 minutes early and getting stuck on a closed boarding menu.
        var msUntil = MsUntilNextDeparture;
        if (msUntil > LeadTimeMinutes * 60_000L)
        {
            // Re-check every minute or so; no need to evaluate every frame.
            _idleRetryAfter = DateTime.UtcNow.AddSeconds(30);
            return;
        }

        if (!VNavmesh.Enabled)
        {
            LastError = "vnavmesh not loaded";
            _idleRetryAfter = DateTime.UtcNow.AddSeconds(5);
            return;
        }

        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return;

        // Restock first if configured; otherwise proceed straight to pathing.
        if (Restock.Enabled && Restock.AnyMissing())
        {
            var r = Restock.TryStart();
            var status = $"{r} ({Restock.LastStatus})";
            if (status != _lastLoggedRestockStatus)
            {
                GatherBuddy.Log.Information($"[Embark] Restock pre-check → {status}");
                _lastLoggedRestockStatus = status;
            }
            if (r == BaitRestock.RestockResult.Started || r == BaitRestock.RestockResult.AlreadyRunning)
            {
                Transition(EmbarkState.Restocking);
                return;
            }
            if (r == BaitRestock.RestockResult.NotConfigured || r == BaitRestock.RestockResult.Failed)
            {
                // No point retrying every frame — cool off and let the user fix config.
                LastError = $"restock {r}: {Restock.LastStatus}";
                _idleRetryAfter = DateTime.UtcNow.AddSeconds(10);
                return;
            }
        }

        var dest = ResolveDestination();
        if (dest is null)
        {
            LastError = "no on-mesh destination near Ferry Skipper (NPC out of stream range; configured coords off-mesh)";
            _idleRetryAfter = DateTime.UtcNow.AddSeconds(5);
            return;
        }

        _pathTask = VNavmesh.Nav.Pathfind(player.Position, dest.Value, false);
        Transition(EmbarkState.Pathing);
    }

    // Prefer the live NPC position over the static config — the configured value is a fallback
    // for when the Skipper hasn't streamed in yet, but if he's loaded, his actual position is
    // always on-mesh and always correct (the static value may drift or be off-mesh).
    //
    // If we have to fall back to the static value (NPC out of range), snap it to the nearest
    // on-mesh point via vnavmesh — otherwise Pathfind returns no waypoints and the user gets
    // stuck retrying. Returns null if no on-mesh point can be found within a generous box.
    private Vector3? ResolveDestination()
    {
        var npc = Dalamud.Objects.FirstOrDefault(o => o.DataId == FerrySkipperDataId);
        if (npc != null)
            return npc.Position;

        var snapped = VNavmesh.Query.Mesh.NearestPoint?.Invoke(FerryStandPosition, 20f, 20f);
        return snapped;
    }

    private void TickRestocking()
    {
        if (Restock.IsRunning)
            return;

        GatherBuddy.Log.Information($"[Embark] Restock done ({Restock.LastStatus}); resuming embark.");

        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
        {
            Transition(EmbarkState.Idle);
            return;
        }
        var dest2 = ResolveDestination();
        if (dest2 is null)
        {
            LastError = "no on-mesh destination after restock";
            _idleRetryAfter = DateTime.UtcNow.AddSeconds(5);
            Transition(EmbarkState.Idle);
            return;
        }
        _pathTask = VNavmesh.Nav.Pathfind(player.Position, dest2.Value, false);
        Transition(EmbarkState.Pathing);
    }

    private void TickPathing()
    {
        if (_pathTask is null)
        {
            Transition(EmbarkState.Idle);
            return;
        }
        if (!_pathTask.IsCompleted)
            return;

        if (_pathTask.IsFaulted)
        {
            LastError = $"pathfind faulted: {_pathTask.Exception?.GetBaseException().Message}";
            _idleRetryAfter = DateTime.UtcNow.AddSeconds(5);
            Transition(EmbarkState.Idle);
            return;
        }

        var waypoints = _pathTask.Result;
        if (waypoints is null || waypoints.Count == 0)
        {
            LastError = "pathfind returned no waypoints (destination off-mesh? try editing Ferry stand position to the live NPC location)";
            _idleRetryAfter = DateTime.UtcNow.AddSeconds(5);
            Transition(EmbarkState.Idle);
            return;
        }

        VNavmesh.Path.MoveTo(waypoints, false);
        Transition(EmbarkState.Moving);
    }

    private void TickMoving()
    {
        // Wait until vnavmesh reports done OR we are close enough to the NPC.
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null) return;

        var dist = Vector3.Distance(player.Position, FerryStandPosition);
        var moving = VNavmesh.Path.IsRunning();

        if (!moving || dist < 3f)
        {
            try { VNavmesh.Path.Stop(); } catch { /* ignore */ }
            Transition(EmbarkState.AtNpc);
        }
    }

    private unsafe void TickAtNpc()
    {
        // Find the Ferry Skipper by data id anywhere in the loaded scene — distance gating
        // against FerryStandPosition just creates a second way to fail when the stand position
        // is slightly off. Pathing already brought us close, and the NPC list is filtered by
        // streaming range, so any match is the right one.
        var npc = Dalamud.Objects.FirstOrDefault(o => o.DataId == FerrySkipperDataId);

        if (npc is null)
        {
            LastError = $"Ferry Skipper (data id {FerrySkipperDataId}) not loaded. " +
                        "Stand next to the real NPC and click 'Dump nearby NPCs' in the debug section to find the right data id.";
            if ((DateTime.UtcNow - _stateEnteredAt).TotalSeconds > 10)
            {
                _idleRetryAfter = DateTime.UtcNow.AddSeconds(5);
                Transition(EmbarkState.Idle);
            }
            return;
        }

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return;

        targetSystem->Target = (GameObject*)npc.Address;
        targetSystem->OpenObjectInteraction((GameObject*)npc.Address);
        Transition(EmbarkState.Interacting);
    }

    private unsafe void TickInteracting()
    {
        // Wait up to a couple seconds for SelectString to appear.
        if (!GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var addon) || !addon->AtkUnitBase.IsVisible)
        {
            if ((DateTime.UtcNow - _stateEnteredAt).TotalSeconds > 5)
            {
                LastError = "SelectString did not appear after interact.";
                Transition(EmbarkState.Idle);
            }
            return;
        }

        Transition(EmbarkState.SelectingRoute);
    }

    private unsafe void TickSelectingRoute()
    {
        if (!GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var addon) || !addon->AtkUnitBase.IsVisible)
        {
            // Menu already dismissed — wait for territory change.
            return;
        }

        var entries = new AddonMaster.SelectString((nint)addon);
        if (SelectStringBoardIndex >= entries.EntryCount)
        {
            LastError = $"SelectStringBoardIndex {SelectStringBoardIndex} out of range (entries={entries.EntryCount}).";
            Transition(EmbarkState.Idle);
            return;
        }

        entries.Entries[SelectStringBoardIndex].Select();
        // Stay in this state; the territory change will flip us to Boarded.
    }

    private void Transition(EmbarkState next)
    {
        if (State == next) return;
        GatherBuddy.Log.Information($"[Embark] {State} → {next}");
        State = next;
        _stateEnteredAt = DateTime.UtcNow;

        if (next == EmbarkState.Idle)
            _pathTask = null;
    }

    public void Dispose()
    {
        Enabled = false;
        try { VNavmesh.Path.Stop?.Invoke(); } catch { /* ignore */ }
    }
}

public enum EmbarkState
{
    Idle,
    Restocking,
    Pathing,
    Moving,
    AtNpc,
    Interacting,
    SelectingRoute,
    Boarded,
}
