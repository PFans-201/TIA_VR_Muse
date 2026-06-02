using System;
using UnityEngine;

/// Singleton bridge between the MUSE S EEG device and the puzzle hint system.
///
/// The MUSE S integration layer should call SetStressLevel(0–1) whenever
/// a new reading arrives. Everything downstream (PieceHintSystem) listens
/// to OnStressChanged and reacts accordingly.
///
/// Setup in Unity Editor:
///   Add this component to any persistent GameObject in the Zen scene.
///   Set hintThreshold to the stress level at which colour hints should activate.
///   In Play mode you can simulate a reading via debugStressOverride in the Inspector.
public class CognitiveLoadAdapter : MonoBehaviour
{
    public static CognitiveLoadAdapter Instance { get; private set; }

    [Tooltip("Stress value (0–1) above which colour hints activate. " +
             "0.65 means hints show when the player is moderately overloaded.")]
    [Range(0f, 1f)]
    public float hintThreshold = 0.65f;

    [SerializeField, Range(0f, 1f)]
    private float _stressLevel;

    public float StressLevel => _stressLevel;
    public bool  HintsActive  => _stressLevel >= hintThreshold;

    /// Fires whenever the stress level changes. Passes the new 0–1 value.
    public event Action<float> OnStressChanged;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    /// Called by the MUSE S integration layer each time a new reading arrives.
    public void SetStressLevel(float level)
    {
        level = Mathf.Clamp01(level);
        if (Mathf.Approximately(_stressLevel, level)) return;
        _stressLevel = level;
        OnStressChanged?.Invoke(_stressLevel);
    }

#if UNITY_EDITOR
    [Header("Debug — Simulate MUSE S in Play Mode")]
    [Tooltip("Drag this slider in the Inspector to simulate a stress reading")]
    [Range(0f, 1f)]
    public float debugStressOverride;
    private float _lastDebug = -1f;

    private void Update()
    {
        if (!Mathf.Approximately(debugStressOverride, _lastDebug))
        {
            _lastDebug = debugStressOverride;
            SetStressLevel(debugStressOverride);
        }
    }
#endif
}
