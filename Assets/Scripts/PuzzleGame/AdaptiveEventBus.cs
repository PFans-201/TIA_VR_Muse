using System;
using UnityEngine;

/// What kind of signal caused an adaptive action to fire.
public enum AdaptiveSignal
{
    MuseStress,   // driven by the Muse S stress reading (EEG)
    Behavior      // driven by in-game behaviour (time on a piece, grab count, lost in the dark…)
}

/// A tiny global channel the adaptive systems report to whenever they take an action
/// (colour hint, magnet/visibility assistance, lantern cone, find-me glow…). The
/// AdaptiveEventHUD subscribes to surface them in-headset so the player AND developers
/// can see WHEN each action happens and BECAUSE OF WHICH signal. Every event is also
/// written to the log (visible in `adb logcat`) regardless of whether a HUD is present.
///
/// Static so callers don't need a scene reference — just AdaptiveEventBus.Report(...).
public static class AdaptiveEventBus
{
    public readonly struct AdaptiveEvent
    {
        public readonly string         action;
        public readonly AdaptiveSignal signal;
        public readonly float          time;     // Time.time when reported

        public AdaptiveEvent(string action, AdaptiveSignal signal, float time)
        {
            this.action = action;
            this.signal = signal;
            this.time   = time;
        }
    }

    /// Raised every time a system reports an adaptive action.
    public static event Action<AdaptiveEvent> OnEvent;

    public static void Report(string action, AdaptiveSignal signal)
    {
        var e = new AdaptiveEvent(action, signal, Time.time);
        Debug.Log($"[Adaptive] {(signal == AdaptiveSignal.MuseStress ? "MUSE" : "GAME")}: {action}");
        OnEvent?.Invoke(e);
    }
}
