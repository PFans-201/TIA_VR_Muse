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
    public CognitiveLoadAdapter cognitiveLoad;
    public PuzzleManager        puzzleManager;

    [Header("Assistance Thresholds")]
    [Tooltip("Stress value at which mechanical assistance (magnetism / visibility) begins.\n" +
             "Should be equal to or higher than CognitiveLoadAdapter.hintThreshold so\n" +
             "colour hints appear before magnetic/visibility help kicks in.")]
    [Range(0f, 1f)]
    public float assistanceOnset = 0.70f;

    [Tooltip("Stress value at which the full Easy-mode settings are applied.\n" +
             "Between assistanceOnset and this value the help scales linearly.")]
    [Range(0f, 1f)]
    public float assistanceMax = 0.95f;

    [Header("Ramp Speed")]
    [Tooltip("How quickly assistance fades IN when stress rises (blend/second).\n" +
             "Higher = faster response to detected overload.")]
    public float rampUpSpeed = 1.5f;

    [Tooltip("How quickly assistance fades OUT when stress drops (blend/second).\n" +
             "Kept slower than ramp-up so help doesn't disappear immediately.")]
    public float rampDownSpeed = 0.35f;

    // ── Runtime state ─────────────────────────────────────────────────────────

    /// Current assistance blend applied to the puzzle (0 = baseline, 1 = full Easy mode).
    public float CurrentBlend => _currentBlend;

    private float _currentBlend;
    private float _targetBlend;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Start()
    {
        if (cognitiveLoad != null)
            cognitiveLoad.OnStressChanged += OnStressChanged;
    }

    private void OnDestroy()
    {
        if (cognitiveLoad != null)
            cognitiveLoad.OnStressChanged -= OnStressChanged;
    }

    private void Update()
    {
        if (puzzleManager == null) return;

        // Move current blend smoothly towards the target
        float speed    = _targetBlend > _currentBlend ? rampUpSpeed : rampDownSpeed;
        float newBlend = Mathf.MoveTowards(_currentBlend, _targetBlend, speed * Time.deltaTime);

        if (!Mathf.Approximately(newBlend, _currentBlend))
        {
            _currentBlend = newBlend;
            puzzleManager.OverrideAssistance(_currentBlend);
        }
    }

    // ── Stress callback ───────────────────────────────────────────────────────

    private void OnStressChanged(float stress)
    {
        // Map the stress range [assistanceOnset … assistanceMax] → [0 … 1]
        _targetBlend = Mathf.Clamp01(
            Mathf.InverseLerp(assistanceOnset, assistanceMax, stress));
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
