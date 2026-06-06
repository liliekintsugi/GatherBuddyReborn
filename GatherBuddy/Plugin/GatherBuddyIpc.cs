using System;
using System.Diagnostics;

namespace GatherBuddy.Plugin;

public sealed class GatherBuddyIpc : IDisposable
{
    public const int IpcVersion = 3;

    private readonly GatherBuddy _plugin;

    public GatherBuddyIpc(GatherBuddy plugin)
    {
        _plugin = plugin;
        EzIPC.Init(this, GatherBuddy.InternalName);
        Debug.Assert(AutoGatherWaiting != null);
        Debug.Assert(AutoGatherEnabledChanged != null);
    }

#pragma warning disable CA1822 // Mark members as static
    [EzIPC]
    public int Version()
        => IpcVersion;

    [EzIPC]
    public uint Identify(string text)
        => _plugin.Executor.Identificator.IdentifyGatherable(text)?.ItemId
         ?? _plugin.Executor.Identificator.IdentifyFish(text)?.ItemId ?? 0;

    [EzIPC]
    public bool IsAutoGatherEnabled()
        => GatherBuddy.AutoGather.Enabled;

    [EzIPC]
    public string GetAutoGatherStatusText()
        => GatherBuddy.AutoGather.AutoStatus;

    [EzIPC]
    public void SetAutoGatherEnabled(bool enabled)
        => GatherBuddy.AutoGather.Enabled = enabled;

    [EzIPC]
    public bool IsAutoGatherWaiting()
        => GatherBuddy.AutoGather.Waiting;

    [EzIPCEvent]
    public Action AutoGatherWaiting;

    [EzIPCEvent]
    public Action<bool> AutoGatherEnabledChanged;

    // --- Ocean fishing (added in IpcVersion 3) ---

    [EzIPC]
    public byte GetCurrentOceanRouteId()
        => GatherBuddy.AutoOceanFishing?.CurrentRoute?.Id ?? (byte)0;

    [EzIPC]
    public string GetCurrentOceanRouteName()
        => GatherBuddy.AutoOceanFishing?.CurrentRoute?.Name ?? string.Empty;

    [EzIPC]
    public int GetCurrentOceanSegment()
        => GatherBuddy.AutoOceanFishing?.CurrentSegment ?? -1;

    [EzIPC]
    public bool IsSpectralActive()
        => GatherBuddy.SpectralDetector?.IsSpectralActive ?? false;

    [EzIPC]
    public bool IsAutoOceanEnabled()
        => GatherBuddy.AutoOceanFishing?.Enabled ?? false;

    [EzIPC]
    public void SetAutoOceanEnabled(bool enabled)
    {
        if (GatherBuddy.AutoOceanFishing != null)
            GatherBuddy.AutoOceanFishing.Enabled = enabled;
    }

    [EzIPC]
    public string GetEmbarkState()
        => GatherBuddy.EmbarkController?.State.ToString() ?? "Unknown";

#pragma warning restore CA1822 // Mark members as static

    public void Dispose()
    {
        // EzIPC disposal is handled in GatherBuddy.cs Dispose method
    }
}
