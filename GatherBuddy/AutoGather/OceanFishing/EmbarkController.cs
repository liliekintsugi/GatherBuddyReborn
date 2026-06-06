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
    public bool Enabled { get; set; } = false;

    // Territory where the Ferry Skipper stands (Limsa Lominsa Lower Decks).
    public ushort FerryTerritoryId { get; set; } = 129;

    // Default ferry coordinates — verified once on a live character, then user-overridable.
    public Vector3 FerryStandPosition { get; set; } = new(-129.7f, 18.0f, 39.9f);

    // Ferry Skipper data id ("dataId" matches GameObject.DataId). Default sourced from community
    // tooling; if it ever changes the user can edit it from the UI.
    public uint FerrySkipperDataId { get; set; } = 1027847;

    // Index into the SelectString menu that boards the next voyage. Game-version dependent; usually 0.
    public int SelectStringBoardIndex { get; set; } = 0;

    // Start walking when next departure is within this many minutes.
    public int LeadTimeMinutes { get; set; } = 3;

    public EmbarkState State { get; private set; } = EmbarkState.Idle;
    public string?     LastError { get; private set; }

    private DateTime _stateEnteredAt = DateTime.UtcNow;
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
        // PR 4 keeps scheduling manual: user toggles Enabled when they're standing in the ferry
        // territory and ready to be walked to the NPC. PR 6 will time-gate this against
        // OceanUptime so the toggle can mean "embark on the next available trip" instead.
        if (Dalamud.ClientState.TerritoryType != FerryTerritoryId)
            return;

        if (!VNavmesh.Enabled)
        {
            LastError = "vnavmesh not loaded";
            return;
        }

        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return;

        _pathTask = VNavmesh.Nav.Pathfind(player.Position, FerryStandPosition, false);
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
            Transition(EmbarkState.Idle);
            return;
        }

        var waypoints = _pathTask.Result;
        if (waypoints is null || waypoints.Count == 0)
        {
            LastError = "pathfind returned no waypoints";
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
        // Find the Ferry Skipper near our standing point.
        var npc = Dalamud.Objects.FirstOrDefault(o =>
            o.DataId == FerrySkipperDataId
         && Vector3.Distance(o.Position, FerryStandPosition) < 10f);

        if (npc is null)
        {
            LastError = $"Ferry Skipper (data id {FerrySkipperDataId}) not found near stand position.";
            // Stay in this state briefly — the NPC may not have loaded yet.
            if ((DateTime.UtcNow - _stateEnteredAt).TotalSeconds > 10)
                Transition(EmbarkState.Idle);
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
    Pathing,
    Moving,
    AtNpc,
    Interacting,
    SelectingRoute,
    Boarded,
}
