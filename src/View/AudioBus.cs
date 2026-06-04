using Godot;

namespace Raptor.View;

/// <summary>
/// Helpers for the per-category audio buses (Music, SFX) that the in-game
/// options sliders drive. Pure View; created lazily at runtime so no bus-layout
/// resource is needed.
/// </summary>
internal static class AudioBus
{
    /// <summary>Index of a named bus, creating it (routed to Master) if absent.</summary>
    public static int Ensure(string name)
    {
        int idx = AudioServer.GetBusIndex(name);
        if (idx >= 0) return idx;
        AudioServer.AddBus();
        idx = AudioServer.BusCount - 1;
        AudioServer.SetBusName(idx, name);
        AudioServer.SetBusSend(idx, "Master");
        return idx;
    }

    /// <summary>
    /// Drive a bus's volume from a 0..127 options-slider value (C opt_vol range):
    /// 127 → 0 dB + <paramref name="trimDb"/>, 0 → muted. <paramref name="trimDb"/>
    /// lets SFX sit a few dB below digital full so the rapid gun doesn't blast.
    /// </summary>
    public static void SetVolume(string name, int vol0to127, float trimDb)
    {
        int idx = AudioServer.GetBusIndex(name);
        if (idx < 0) return;
        if (vol0to127 <= 0)
        {
            AudioServer.SetBusMute(idx, true);
            return;
        }
        AudioServer.SetBusMute(idx, false);
        float frac = Mathf.Clamp(vol0to127 / 127f, 0.0001f, 1f);
        AudioServer.SetBusVolumeDb(idx, Mathf.LinearToDb(frac) + trimDb);
    }
}
