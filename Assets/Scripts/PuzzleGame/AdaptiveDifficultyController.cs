using UnityEngine;

/// Watches the MUSE S stress signal and temporarily eases puzzle conditions
/// when the player is detected as cognitively overloaded.
///
/// The "difficulty" the player picks at the start is a BASELINE — it expresses
/// how much help they want by default. This controller sits on top of that and
/// automatically increases assistance further when signals indicate overload,
/// then retreats back to the chosen baseline once the player recovers.
///
/// Assistance escalates in two stages as stress rises:
///
///   Stage 0  (stress < hintOnset)
///     → No extra help. Piece colours, magnetism and ghost visibility are
///       exactly as the player configured.
///
///   Stage 1  (hintOnset ≤ stress < assistanceOnset)
///     → Colour hints only (handled by PieceHintSystem / CognitiveLoadAdapter).
///       Mechanical settings unchanged.
///
///   Stage 2  (stress ≥ assistanceOnset)
///     → Magnetic force, piece visibility and ghost alpha all gradually
///       increase towards their Easy-mode (maximum assistance) values.
///       The further above the threshold, the stronger the boost.
///       When stress drops, assistance fades back out — slowly, so the
///       transition feels natural and not jarring.
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
    }

    private void OnDestroy()
    {
        if (cognitiveLoad != null)
            cognitiveLoad.OnStressChanged -= OnStressChanged;
        if (stressDetector != null)
            stressDetector.OnStateChanged -= OnStressStateChanged;
    }

    private void Update()
    {
        if (puzzleManager == null) return;

        float speed    = _targetBlend > _currentBlend ? rampUpSpeed : rampDownSpeed;
        float newBlend = Mathf.MoveTowards(_currentBlend, _targetBlend, speed * Time.deltaTime);

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
        _sustainedStressActive = (state == StressState.Stressed);
        if (!_sustainedStressActive)
            _targetBlend = 0f;   // start ramping down immediately on recovery
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
