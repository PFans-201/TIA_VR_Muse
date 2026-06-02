using System;
using System.Collections.Generic;
using UnityEngine;

public enum StressState { Calm, Stressed }

/// Converts the continuous stress signal from CognitiveLoadAdapter into a
/// discrete Calm / Stressed state, using two criteria to avoid reacting to
/// transient noise spikes:
///
///   1. TEMPORAL PERSISTENCE — the rolling mean must exceed the onset threshold
///      for a minimum number of seconds before state changes.
///
///   2. SIGNAL STABILITY — the variance of the rolling window must be low.
///      A genuine sustained-load signal is relatively stable; random noise spikes
///      produce high variance. If the user is consistently above threshold but the
///      signal is jittery, the state change is deferred until the signal settles.
///
/// Hysteresis prevents rapid back-and-forth: entering Stressed requires mean >
/// onsetThreshold; returning to Calm requires mean < recoveryThreshold (lower).
///
/// The AdaptiveDifficultyController listens to OnStateChanged and only enables
/// mechanical assistance (magnets, visibility) when Stressed is confirmed.
public class SustainedStressDetector : MonoBehaviour
{
    [Header("References")]
    public CognitiveLoadAdapter cognitiveLoad;

    [Header("Onset — entering Stressed")]
    [Tooltip("Rolling mean must exceed this before onset counter starts.")]
    [Range(0f, 1f)] public float onsetThreshold = 0.60f;

    [Tooltip("Rolling variance must be below this for onset to be counted.\n" +
             "Prevents reacting to noisy transient spikes.\n" +
             "0.04 means the std of the window is ~0.2, which is quite stable.")]
    [Range(0f, 0.20f)] public float maxVarianceForOnset = 0.04f;

    [Tooltip("Onset conditions must be met continuously for this many seconds.")]
    [Range(5f, 90f)] public float minOnsetSeconds = 20f;

    [Header("Recovery — returning to Calm")]
    [Tooltip("Rolling mean must drop below this (lower than onset = hysteresis gap).")]
    [Range(0f, 1f)] public float recoveryThreshold = 0.40f;

    [Tooltip("Recovery condition must be met continuously for this many seconds.")]
    [Range(5f, 60f)] public float minRecoverySeconds = 15f;

    [Header("Rolling Window")]
    [Tooltip("Width of the rolling time window used to compute mean and variance.")]
    [Range(10f, 90f)] public float windowSeconds = 30f;

    // ── Public state ──────────────────────────────────────────────────────────

    public StressState CurrentState    => _state;
    public float       RollingMean     => _rollingMean;
    public float       RollingVariance => _rollingVariance;
    public float       OnsetProgress   => minOnsetSeconds > 0f ? _onsetSeconds / minOnsetSeconds : 0f;

    /// Fires whenever the state flips between Calm and Stressed.
    public event Action<StressState> OnStateChanged;

    // ── Inspector debug ───────────────────────────────────────────────────────

    [Header("Live State (read-only)")]
    [SerializeField] private StressState _state = StressState.Calm;
    [SerializeField] private float       _rollingMean;
    [SerializeField] private float       _rollingVariance;
    [SerializeField] private float       _onsetSeconds;
    [SerializeField] private float       _recoverySeconds;

    private readonly List<(float t, float v)> _buffer = new();
    private float _lastSampleTime = -1f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        if (cognitiveLoad != null)
            cognitiveLoad.OnStressChanged += OnStressReceived;
        else
            Debug.LogWarning("[SustainedStressDetector] cognitiveLoad not assigned.");
    }

    private void OnDestroy()
    {
        if (cognitiveLoad != null)
            cognitiveLoad.OnStressChanged -= OnStressReceived;
    }

    // ── Stress callback ───────────────────────────────────────────────────────

    private void OnStressReceived(float stress)
    {
        float now = Time.time;
        float dt  = _lastSampleTime >= 0f ? now - _lastSampleTime : 2f;
        _lastSampleTime = now;

        _buffer.Add((now, stress));

        // Prune samples outside the rolling window
        _buffer.RemoveAll(e => now - e.t > windowSeconds);

        if (_buffer.Count < 3) return;   // wait until we have at least 3 samples

        // ── Rolling statistics ────────────────────────────────────────────────
        float sum = 0, sumSq = 0;
        foreach (var e in _buffer) { sum += e.v; sumSq += e.v * e.v; }
        int   n = _buffer.Count;
        _rollingMean     = sum / n;
        _rollingVariance = (sumSq / n) - (_rollingMean * _rollingMean);

        // ── State machine ─────────────────────────────────────────────────────
        switch (_state)
        {
            case StressState.Calm:
                bool onsetCondition = _rollingMean > onsetThreshold
                                   && _rollingVariance < maxVarianceForOnset;
                if (onsetCondition)
                {
                    _onsetSeconds += dt;
                    if (_onsetSeconds >= minOnsetSeconds)
                        Transition(StressState.Stressed);
                }
                else
                {
                    _onsetSeconds = Mathf.Max(0f, _onsetSeconds - dt);  // decay, don't hard-reset
                }
                break;

            case StressState.Stressed:
                if (_rollingMean < recoveryThreshold)
                {
                    _recoverySeconds += dt;
                    if (_recoverySeconds >= minRecoverySeconds)
                        Transition(StressState.Calm);
                }
                else
                {
                    _recoverySeconds = 0f;
                }
                break;
        }
    }

    private void Transition(StressState next)
    {
        _state           = next;
        _onsetSeconds    = 0f;
        _recoverySeconds = 0f;
        Debug.Log($"[SustainedStressDetector] → {next}  " +
                  $"mean={_rollingMean:F2}  var={_rollingVariance:F4}");
        OnStateChanged?.Invoke(next);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (recoveryThreshold >= onsetThreshold)
            recoveryThreshold = onsetThreshold - 0.10f;
    }
#endif
}
