using System;
using System.Numerics;

namespace GatherBuddy.Config;

// Persisted state for the ocean-fishing pipeline. All runtime ocean-fishing classes
// (EmbarkController / BaitRestock / LevelingMode / BaitGuard / AutoOceanFishing) read and
// write this slice so that toggles, NPC ids, presets etc. survive across sessions instead of
// being reset to defaults at every plugin load.
public sealed class OceanFishingConfig
{
    // EmbarkController
    public bool       EmbarkEnabled          { get; set; } = false;
    public Vector3    FerryStandPosition     { get; set; } = new(-409.9f, 4.0f, 76.0f);
    public uint       FerrySkipperDataId     { get; set; } = 1005421;
    public ushort     FerryTerritoryId       { get; set; } = 129;
    public int        SelectStringBoardIndex { get; set; } = 0;
    public int        LeadTimeMinutes        { get; set; } = 15;

    // BaitRestock
    public bool       RestockEnabled         { get; set; } = false;
    public Guid?      RestockBuyListId       { get; set; }

    // LevelingMode
    public bool       LevelingEnabled        { get; set; } = false;
    public int        LevelingLevelMin       { get; set; } = -3;
    public int        LevelingLevelMax       { get; set; } = 2;
    public int        LevelingRetargetEveryMin { get; set; } = 5;
    public string     LevelingAutoHookPreset { get; set; } = string.Empty;

    // BaitGuard
    public bool       BaitGuardEnabled                { get; set; } = false;
    public bool       BaitGuardOnlyWhenAutoGather     { get; set; } = true;
    public float      BaitGuardTriggerBelowFraction   { get; set; } = 0.3f;

    // AutoOceanFishing
    public bool       AutoOceanEnabled               { get; set; } = true;
    public bool       AutoOceanManageAutoHookState   { get; set; } = true;
    public byte       PreferredOceanArea             { get; set; } = 0;

    // OnBoard navigator (PR 9b)
    public bool       OnBoardNavigatorEnabled        { get; set; } = false;
    public Vector3    OnBoardFishingSpot             { get; set; } = Vector3.Zero;
    public int        OnBoardSettleDelaySeconds      { get; set; } = 4;
    public bool       OnBoardAutoCast                { get; set; } = true;
}
