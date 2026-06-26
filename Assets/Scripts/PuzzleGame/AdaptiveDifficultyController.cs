using UnityEngine;

/// Watches the MUSE S stress signal and temporarily eases puzzle conditions
/// when the player is detected as cognitively overloaded.
///
/// The "difficulty" the player picks at the start is a BASELINE — it expresses
/// how much help they want by default. This controller sits on top of that and
/// automatically increases assistance further when signals indicate overload,
/// then retreats back to the chosen baseline once the player recovers.
///
/// While the sustained-stress detector reports Stressed, this controller continuously eases the
/// active pieces toward their Easy-mode settings — stronger/wider magnet, brighter pieces, clearer
/// ghost silhouettes — scaled by how far above assistanceOnset stress is, fading back out slowly on
/// recovery (see PuzzleManager.OverrideAssistance).
///
/// Setup in Unity Editor:
///   The PuzzleSceneBuilder adds this component automatically.
///   Adjust thresholds and ramp speeds in the Inspector if needed.
[DefaultExecutionOrder(10)]   // run after PuzzleManager (order 0)
public class AdaptiveDifficultyController : MonoBehaviour
{
    [Header("References")]
    public CognitiveLoadAdapter    cognitiveLoad;
    public PuzzleManager           puzzleManager;

    [Header("Sustained Stress Gate (optional but recommended)")]
    [Tooltip("When set, mechanical assistance only activates while the detector reports\n" +
             "a Stressed state (sustained, low-variance elevation).\n" +
             "This prevents brief noise spikes from triggering help.\n" +
             "If null, falls back to the direct stress-level threshold below.")]
    public SustainedStressDetector stressDetector;

    [Header("Assistance Thresholds (used when stressDetector is null)")]
    [Tooltip("Stress value at which mechanical assistance (magnetism / visibility) begins.")]
    [Range(0f, 1f)]
    public float assistanceOnset = 0.70f;

    [Tooltip("Stress value at which the full Easy-mode settings are applied.")]
    [Range(0f, 1f)]
    public float assistanceMax = 0.95f;

    [Header("Ramp Speed")]
    [Tooltip("How quickly assistance fades IN when sustained stress is detected (blend/second).")]
    public float rampUpSpeed = 1.5f;

    [Tooltip("How quickly assistance fades OUT when stress recovers (blend/second).\n" +
             "Kept slower so help doesn't disappear the moment stress briefly dips.")]
    public float rampDownSpeed = 0.35f;

    // ── Runtime state ─────────────────────────────────────────────────────────

    public float CurrentBlend => _currentBlend;

    private float _currentBlend;
    private float _targetBlend;
    private bool  _sustainedStressActive;   // set by stressDetector events

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Start()
    {
        if (cognitiveLoad != null)
            cognitiveLoad.OnStressChanged += OnStressChanged;

        if (stressDetector != null)
            stressDetector.OnStateChanged += OnStressStateChanged;

        if (puzzleManager != null)
            puzzleManager.OnPuzzleStarted += HandlePuzzleStarted;
    }

    private void OnDestroy()
    {
        if (cognitiveLoad != null)
            cognitiveLoad.OnStressChanged -= OnStressChanged;
        if (stressDetector != null)
            stressDetector.OnStateChanged -= OnStressStateChanged;
        if (puzzleManager != null)
            puzzleManager.OnPuzzleStarted -= HandlePuzzleStarted;
    }

    /// Every puzzle begins with NO accumulated easing. Without this, a stressed episode from a previous
    /// puzzle (or from testing in the menu) leaves _currentBlend high, and the leftover assistance is
    /// slammed onto the pieces the instant the next puzzle starts. Resetting here, plus restarting the
    /// detector's onset, means easing only builds up from sustained stress INSIDE this puzzle.
    private void HandlePuzzleStarted(DifficultyLevel _)
    {
        _sustainedStressActive = false;
        _targetBlend           = 0f;
        _currentBlend          = 0f;
        stressDetector?.ResetState();
        if (puzzleManager != null) puzzleManager.OverrideAssistance(0f);
    }

    private void Update()
    {
        if (puzzleManager == null) return;

        // Muse helper switched off in the session menu → ramp assistance back to zero (and keep it
        // there) regardless of stress. Re-enabling restores the live stress-driven target.
        float target   = AssistanceSettings.MuseHelperEnabled ? _targetBlend : 0f;
        float speed    = target > _currentBlend ? rampUpSpeed : rampDownSpeed;
        float newBlend = Mathf.MoveTowards(_currentBlend, target, speed * Time.deltaTime);

        if (!Mathf.Approximately(newBlend, _currentBlend))
        {
            _currentBlend = newBlend;
            puzzleManager.OverrideAssistance(_currentBlend);
        }
    }

    // ── Stress callbacks ──────────────────────────────────────────────────────

    private void OnStressChanged(float stress)
    {
        if (stressDetector != null)
        {
            // Gated mode: only ramp assistance while detector reports Stressed.
            // When Stressed, scale assistance by how far above onset stress is.
            // When Calm, target returns to 0 (regardless of instantaneous stress value).
            if (_sustainedStressActive)
                _targetBlend = Mathf.Clamp01(
                    Mathf.InverseLerp(assistanceOnset, assistanceMax, stress));
            else
                _targetBlend = 0f;
        }
        else
        {
            // Ungated fallback: direct mapping (original behaviour).
            _targetBlend = Mathf.Clamp01(
                Mathf.InverseLerp(assistanceOnset, assistanceMax, stress));
        }
    }

    private void OnStressStateChanged(StressState state)
    {
        bool wasActive = _sustainedStressActive;
        _sustainedStressActive = (state == StressState.Stressed);
        if (!_sustainedStressActive)
            _targetBlend = 0f;   // start ramping down immediately on recovery

        // Stay silent while the Muse helper is off (no easing happens) OR while no puzzle is running
        // (menu / between puzzles) — otherwise an "easing puzzle" banner could pop up off-puzzle.
        if (AssistanceSettings.MuseHelperEnabled && puzzleManager != null && puzzleManager.IsPuzzleActive)
        {
            if (_sustainedStressActive && !wasActive)
                AdaptiveEventBus.Report("Easing puzzle — stronger magnet + clearer pieces", AdaptiveSignal.MuseStress);
            else if (!_sustainedStressActive && wasActive)
                AdaptiveEventBus.Report("Stress recovered — assistance fading back", AdaptiveSignal.MuseStress);
        }

        Debug.Log($"[AdaptiveDifficultyController] Stress state → {state}  " +
                  $"blend target={_targetBlend:F2}");
    }

    // ── Editor helper ─────────────────────────────────────────────────────────
#if UNITY_EDITOR
    private void OnValidate()
    {
        // Ensure sensible ordering: onset must be below max
        if (assistanceMax <= assistanceOnset)
            assistanceMax = assistanceOnset + 0.05f;
    }
#endif
}
