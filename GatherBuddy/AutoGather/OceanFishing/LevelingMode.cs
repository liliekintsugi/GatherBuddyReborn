using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather.OceanFishing;

// PR 8 — "leveling mode": when nothing else is running, auto-fish at the closest fishing spot whose
// gathering level matches the player's level. Sits between ocean trips.
//
// We don't fork AutoGather. Instead we:
//   1. Pick a target FishingSpot from GameData.FishingSpots (exclude spearfish, filter by level).
//   2. Materialize a real AutoGatherList named "GBR Leveling Lv{N}" with that spot's fishes, mark
//      it `Enabled = true`, push via AutoGatherListsManager.AddList.
//   3. Flip GatherBuddy.AutoGather.Enabled = true. AutoGather's own pipeline (path / cast / hook
//      via AutoHook integration) takes over.
//   4. On disable, find & delete that list, leave AutoGather as the user had it.
//
// Trip handoff: while territory == 900 OR EmbarkController is mid-state, we pause the leveling list
// (Enabled = false) so AutoGather doesn't fight the ocean pipeline.
public sealed class LevelingMode
{
    public const string ListNamePrefix = "GBR Leveling Lv";

    public bool Enabled { get; set; } = false;

    // Window relative to player level: include spots with GatheringLevel ∈ [playerLvl + LevelMin, playerLvl + LevelMax].
    public int LevelMin { get; set; } = -3;
    public int LevelMax { get; set; } = 2;

    // Quantity per fish in the generated list. Capped under uint.MaxValue; the list is meant to run
    // indefinitely so a large number works fine.
    public uint QuantityPerFish { get; set; } = 9999;

    // How often we re-pick the target spot. Avoids thrashing if the player levels mid-session.
    public TimeSpan RetargetEvery { get; set; } = TimeSpan.FromMinutes(5);

    // Optional AutoHook preset name to select while leveling — typically a "catch everything,
    // hook on any bite" preset. When empty, we don't touch the user's current preset.
    public string AutoHookPreset { get; set; } = string.Empty;

    private string? _savedAutoHookPreset;

    public FishingSpot? CurrentTargetSpot { get; private set; }
    public AutoGatherList? CurrentList     { get; private set; }
    public string LastStatus               { get; private set; } = string.Empty;
    public DateTime LastRetargetUtc        { get; private set; } = DateTime.MinValue;

    public void Tick()
    {
        var mgr = GatherBuddy.AutoGatherLists;
        if (mgr == null)
            return;

        var inOceanTrip = Dalamud.ClientState.TerritoryType == SpectralDetector.OceanFishingTerritoryId;
        var embarkBusy  = GatherBuddy.EmbarkController != null
                       && GatherBuddy.EmbarkController.State is not EmbarkState.Idle
                       and not EmbarkState.Boarded;

        if (!Enabled || inOceanTrip || embarkBusy)
        {
            // Disable our list so AutoGather doesn't keep fishing levels during ocean / embark.
            if (CurrentList != null && CurrentList.Enabled)
            {
                CurrentList.Enabled = false;
                mgr.SetActiveItems();
                LastStatus = inOceanTrip ? "Paused (on ocean trip)."
                            : embarkBusy ? $"Paused ({GatherBuddy.EmbarkController!.State})."
                            : "Paused (disabled).";
            }
            return;
        }

        // Periodic retarget.
        if (CurrentTargetSpot == null || DateTime.UtcNow - LastRetargetUtc > RetargetEvery)
            Retarget();

        if (CurrentList == null || !CurrentList.Enabled)
        {
            if (CurrentList != null)
            {
                CurrentList.Enabled = true;
                mgr.SetActiveItems();
                LastStatus = $"Resumed list '{CurrentList.Name}'.";
                TrySelectAutoHookPreset();
            }
        }
    }

    private void TrySelectAutoHookPreset()
    {
        if (string.IsNullOrWhiteSpace(AutoHookPreset))
            return;
        if (_savedAutoHookPreset == AutoHookPreset)
            return;
        try
        {
            IPCSubscriber.AutoHook.SetPreset?.Invoke(AutoHookPreset);
            _savedAutoHookPreset = AutoHookPreset;
            GatherBuddy.Log.Information($"[Leveling] AutoHook preset → '{AutoHookPreset}'");
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"[Leveling] couldn't set AutoHook preset '{AutoHookPreset}': {e.Message}");
        }
    }

    public void Retarget()
    {
        var mgr = GatherBuddy.AutoGatherLists;
        if (mgr == null)
        {
            LastStatus = "AutoGatherLists not loaded.";
            return;
        }

        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
        {
            LastStatus = "No local player.";
            return;
        }

        var lvl = player.Level;
        var lo  = System.Math.Max(1, lvl + LevelMin);
        var hi  = System.Math.Min(100, lvl + LevelMax);

        // Pick the highest level spot in window, preferring spots with an aetheryte for teleport.
        var spot = GatherBuddy.GameData.FishingSpots.Values
            .Where(s => !s.Spearfishing)
            .Where(s => s.GatheringLevel >= lo && s.GatheringLevel <= hi)
            .Where(s => s.Items.Length > 0)
            .OrderByDescending(s => s.ClosestAetheryte != null ? 1 : 0)
            .ThenByDescending(s => s.GatheringLevel)
            .FirstOrDefault();

        if (spot == null)
        {
            LastStatus = $"No spot found in level window [{lo},{hi}].";
            return;
        }

        // Tear down a previous list if we had one.
        if (CurrentList != null)
        {
            try { mgr.DeleteList(CurrentList); }
            catch (Exception e) { GatherBuddy.Log.Warning($"[Leveling] couldn't delete old list: {e.Message}"); }
            CurrentList = null;
        }

        var list = new AutoGatherList
        {
            Name        = $"{ListNamePrefix}{spot.GatheringLevel} {spot.Name}".Trim(),
            Description = "Generated by GBR Leveling Mode. Auto-deleted on toggle-off.",
            Enabled     = true,
        };
        foreach (var fish in spot.Items)
        {
            list.Add(fish, QuantityPerFish);
            // Without this, AutoGather is free to pick any spot that contains the fish — usually
            // the closest one to the player, which is rarely our intended target spot. Pin every
            // entry to the spot we just chose so the displayed name and the actual destination
            // stay consistent.
            list.SetPreferredLocation(fish, spot);
        }

        mgr.AddList(list);
        CurrentList       = list;
        CurrentTargetSpot = spot;
        LastRetargetUtc   = DateTime.UtcNow;
        LastStatus        = $"Target: {spot.Name} (Lv{spot.GatheringLevel}, {spot.Items.Length} fish).";

        // Make sure AutoGather is enabled — the list won't do anything by itself.
        if (GatherBuddy.AutoGather != null && !GatherBuddy.AutoGather.Enabled)
            GatherBuddy.AutoGather.Enabled = true;
    }

    public void Cleanup()
    {
        var mgr = GatherBuddy.AutoGatherLists;
        if (mgr == null || CurrentList == null)
            return;
        try { mgr.DeleteList(CurrentList); }
        catch (Exception e) { GatherBuddy.Log.Warning($"[Leveling] cleanup failed: {e.Message}"); }
        CurrentList       = null;
        CurrentTargetSpot = null;
    }
}
