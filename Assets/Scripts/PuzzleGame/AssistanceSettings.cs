using System;

/// Runtime, player-facing on/off switches for the two adaptive "helpers", toggled from the
/// in-game SessionMenu (≡):
///
///   • Behaviour helper  — the colour-match + blink piece hints (PieceHintSystem), which fire from
///                          in-game behaviour: holding a piece too long, or stalling with no progress.
///   • Muse helper       — everything driven by the live MUSE S stress reading: the mechanical
///                          assistance ramp (AdaptiveDifficultyController: stronger magnet + clearer
///                          pieces), the stress-routed piece hints, and the lantern's reactive cone.
///
/// Both default ON. They are independent: turning the Muse helper off still lets behaviour hints
/// appear (just never accelerated by stress); turning the behaviour helper off silences the hints
/// entirely while the Muse mechanical assistance can still kick in.
///
/// Static so any system can read it without scene wiring. Reset() restores defaults for a fresh
/// participant (called when the SessionMenu rebuilds on scene load).
public static class AssistanceSettings
{
    public static bool BehaviorHelperEnabled { get; private set; } = true;
    public static bool MuseHelperEnabled     { get; private set; } = true;

    /// Fired whenever either switch changes, so live systems / UI can react.
    public static event Action OnChanged;

    public static void SetBehaviorHelper(bool on)
    {
        if (BehaviorHelperEnabled == on) return;
        BehaviorHelperEnabled = on;
        OnChanged?.Invoke();
    }

    public static void SetMuseHelper(bool on)
    {
        if (MuseHelperEnabled == on) return;
        MuseHelperEnabled = on;
        OnChanged?.Invoke();
    }

    /// Restore both helpers to ON (fresh participant).
    public static void Reset()
    {
        bool changed = !BehaviorHelperEnabled || !MuseHelperEnabled;
        BehaviorHelperEnabled = true;
        MuseHelperEnabled     = true;
        if (changed) OnChanged?.Invoke();
    }
}
