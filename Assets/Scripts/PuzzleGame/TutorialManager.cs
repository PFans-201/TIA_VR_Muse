using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// Manages the VR onboarding tutorial, guiding the participant through
/// VR controls practice and EEG baseline recording.
///
/// WHY TWO BASELINES?
/// ──────────────────
/// A single resting baseline is not sufficient because:
///   • First-time VR users experience mild arousal from the novelty of the
///     headset — this appears as elevated frontal theta / suppressed alpha.
///   • If we compare puzzle-task EEG against a "sitting-still" baseline,
///     the VR-novelty response inflates the cognitive-load estimate and can
///     trigger false-positive stress detections in the first few minutes.
///
/// Solution: record TWO baselines in sequence.
///
///   1. REST baseline (60 s) — participant stands still with eyes open,
///      no task, no interaction.  Captures their absolute resting state.
///
///   2. ACTIVE baseline (60 s) — participant freely interacts with tutorial
///      objects while told to stay mentally relaxed.  Captures the
///      "VR environment + mild motor activity" level without cognitive load.
///
/// The ACTIVE baseline is sent to MuseAthenaAdapter as the primary reference
/// for the puzzle session.  Puzzle-task stress is then measured as: how much
/// does the EEG deviate above the baseline of just being-in-VR-and-grabbing?
///
/// SCENE FLOW
/// ──────────
///  Welcome → RestBaseline → VRTutorial → ActiveBaseline → Complete
///  (each phase advances automatically or via the "Continue" button)
///
/// WIRING
/// ──────
///  TutorialSceneBuilder creates this component and wires all references.
///  The MuseAthenaAdapter instance persists into the puzzle scene
///  (DontDestroyOnLoad) carrying the recorded baselines.
public class TutorialManager : MonoBehaviour
{
    public enum Phase
    {
        Welcome,
        RestBaseline,
        VRTutorial,
        ActiveBaseline,
        Complete
    }

    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Scene")]
    [Tooltip("Name of the puzzle scene to load after the tutorial completes.")]
    public string puzzleSceneName = "ZenPuzzleRoom";

    [Header("Baseline Durations")]
    [Range(30f, 180f)] public float restBaselineDuration   = 60f;
    [Range(30f, 180f)] public float activeBaselineDuration = 60f;

    [Header("References — EEG")]
    [Tooltip("Assigned automatically if MuseAthenaAdapter.Instance is available.")]
    public MuseAthenaAdapter museAdapter;

    [Header("References — Tutorial Objects")]
    [Tooltip("GameObjects to enable during VR tutorial (grab practice items).")]
    public List<GameObject> tutorialGrabObjects = new();
    [Tooltip("Visual cue shown on the floor during rest baseline.")]
    public GameObject       restZoneMarker;

    [Header("References — UI")]
    public GameObject       instructionPanel;
    public TextMeshProUGUI  instructionTitle;
    public TextMeshProUGUI  instructionBody;
    public Slider           progressBar;
    public Button           continueButton;
    public TextMeshProUGUI  continueLabel;

    // ── State ─────────────────────────────────────────────────────────────────

    [Header("Live State (read-only)")]
    [SerializeField] private Phase  _phase = Phase.Welcome;
    [SerializeField] private float  _phaseProgress;
    [SerializeField] private int    _baselineSamplesCollected;

    // Band-power samples collected during each baseline phase
    private readonly List<double[]> _restSamples   = new();
    private readonly List<double[]> _activeSamples = new();

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Start()
    {
        // Find the persisted adapter singleton if not wired in Inspector
        if (museAdapter == null)
            museAdapter = MuseAthenaAdapter.Instance;

        // Tutorial objects hidden until VRTutorial phase
        SetTutorialObjectsActive(false);
        if (restZoneMarker != null) restZoneMarker.SetActive(false);

        if (continueButton != null)
            continueButton.onClick.AddListener(AdvanceFromWelcome);

        ShowPhase(Phase.Welcome);
    }

    // ── Phase navigation ──────────────────────────────────────────────────────

    private void AdvanceFromWelcome()
    {
        if (_phase != Phase.Welcome) return;
        if (continueButton != null) continueButton.gameObject.SetActive(false);
        StartCoroutine(RunRestBaseline());
    }

    private IEnumerator RunRestBaseline()
    {
        ShowPhase(Phase.RestBaseline);
        _restSamples.Clear();
        if (restZoneMarker != null) restZoneMarker.SetActive(true);

        float elapsed = 0f;
        while (elapsed < restBaselineDuration)
        {
            yield return new WaitForSeconds(2f);
            elapsed += 2f;

            TrySampleBandPowers(_restSamples);

            _phaseProgress              = Mathf.Clamp01(elapsed / restBaselineDuration);
            _baselineSamplesCollected   = _restSamples.Count;
            UpdateProgress(_phaseProgress,
                $"REST baseline: {elapsed:F0} / {restBaselineDuration:F0} s" +
                $"  ({_restSamples.Count} windows)");
        }

        if (restZoneMarker != null) restZoneMarker.SetActive(false);
        StartCoroutine(RunVRTutorial());
    }

    private IEnumerator RunVRTutorial()
    {
        ShowPhase(Phase.VRTutorial);
        SetTutorialObjectsActive(true);

        // Wait for the participant to click "Continue" after practicing
        if (continueButton != null)
        {
            continueButton.gameObject.SetActive(true);
            if (continueLabel != null) continueLabel.text = "I'm ready — Continue";
            continueButton.onClick.RemoveAllListeners();
            bool advanced = false;
            continueButton.onClick.AddListener(() => advanced = true);
            yield return new WaitUntil(() => advanced);
            continueButton.gameObject.SetActive(false);
        }
        else
        {
            // Fallback: auto-advance after 2 minutes
            yield return new WaitForSeconds(120f);
        }

        StartCoroutine(RunActiveBaseline());
    }

    private IEnumerator RunActiveBaseline()
    {
        ShowPhase(Phase.ActiveBaseline);
        _activeSamples.Clear();

        float elapsed = 0f;
        while (elapsed < activeBaselineDuration)
        {
            yield return new WaitForSeconds(2f);
            elapsed += 2f;

            TrySampleBandPowers(_activeSamples);

            _phaseProgress            = Mathf.Clamp01(elapsed / activeBaselineDuration);
            _baselineSamplesCollected = _activeSamples.Count;
            UpdateProgress(_phaseProgress,
                $"ACTIVE baseline: {elapsed:F0} / {activeBaselineDuration:F0} s" +
                $"  ({_activeSamples.Count} windows)");
        }

        // Apply baselines to the adapter before leaving
        ApplyBaselines();
        StartCoroutine(CompleteAndLoad());
    }

    private IEnumerator CompleteAndLoad()
    {
        ShowPhase(Phase.Complete);
        yield return new WaitForSeconds(3f);
        SceneManager.LoadScene(puzzleSceneName);
    }

    // ── Baseline helpers ──────────────────────────────────────────────────────

    private void TrySampleBandPowers(List<double[]> target)
    {
        if (museAdapter == null || !museAdapter.HasValidData) return;
        double[] bp = museAdapter.LatestBandPowers;
        if (bp != null && bp.Length >= 5)
            target.Add((double[])bp.Clone());
    }

    private void ApplyBaselines()
    {
        if (museAdapter == null) return;

        // Rest baseline: informational, stored for reference
        if (_restSamples.Count > 5)
        {
            ComputeMeanStd(_restSamples, out double[] rMean, out double[] rStd);
            Debug.Log($"[TutorialManager] Rest baseline: θ={rMean[1]:F3}±{rStd[1]:F3}  " +
                      $"α={rMean[2]:F3}±{rStd[2]:F3}  ({_restSamples.Count} windows)");
        }

        // Active baseline: the primary reference for puzzle-task stress detection.
        // This captures "being in VR + mild motor activity WITHOUT cognitive load".
        if (_activeSamples.Count > 5)
        {
            ComputeMeanStd(_activeSamples, out double[] aMean, out double[] aStd);
            museAdapter.OverrideBaseline(aMean, aStd);
            Debug.Log($"[TutorialManager] Active baseline applied to adapter: " +
                      $"θ={aMean[1]:F3}±{aStd[1]:F3}  α={aMean[2]:F3}±{aStd[2]:F3}");
        }
        else
        {
            Debug.LogWarning("[TutorialManager] Not enough active baseline samples — " +
                             "adapter will use its own auto-baseline.");
        }
    }

    private static void ComputeMeanStd(List<double[]> samples,
                                        out double[] mean, out double[] std)
    {
        int n     = samples.Count;
        int nBand = samples[0].Length;
        mean = new double[nBand];
        std  = new double[nBand];

        for (int b = 0; b < nBand; b++)
        {
            double sum = 0;
            foreach (var s in samples) sum += s[b];
            mean[b] = sum / n;

            double variance = 0;
            foreach (var s in samples) variance += (s[b] - mean[b]) * (s[b] - mean[b]);
            std[b] = Math.Sqrt(variance / n);
        }
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void ShowPhase(Phase p)
    {
        _phase         = p;
        _phaseProgress = 0f;
        if (progressBar != null) progressBar.value = 0f;

        string title, body;
        switch (p)
        {
            case Phase.Welcome:
                title = "Welcome";
                body  = "You are about to experience a VR puzzle game.\n\n" +
                        "First, we'll guide you through the controls and record\n" +
                        "a brief resting measurement with the EEG headset.\n\n" +
                        "Use your controllers to reach out and GRAB objects.\n" +
                        "You can MOVE by pressing the thumbstick.\n\n" +
                        "Press CONTINUE when you are ready.";
                if (continueButton != null)
                {
                    continueButton.gameObject.SetActive(true);
                    if (continueLabel != null) continueLabel.text = "Continue";
                }
                break;

            case Phase.RestBaseline:
                title = "Rest Baseline";
                body  = "Please stand or sit comfortably.\n\n" +
                        "Look straight ahead, relax, and try to clear your mind.\n" +
                        "Do NOT do any task — just breathe and rest.\n\n" +
                        "This takes about " + restBaselineDuration.ToString("F0") + " seconds.\n" +
                        "The system is recording your resting brain activity.";
                break;

            case Phase.VRTutorial:
                title = "VR Controls Practice";
                body  = "Now let's practice the controls!\n\n" +
                        "• Reach out and SQUEEZE the trigger to grab a coloured object.\n" +
                        "• RELEASE the trigger to let it go.\n" +
                        "• Try to place the objects on the grey pedestals.\n" +
                        "• You can look around freely — rotate your head.\n\n" +
                        "Take your time. Press CONTINUE when you feel comfortable.";
                break;

            case Phase.ActiveBaseline:
                title = "Activity Baseline";
                body  = "Great! Keep interacting with the objects.\n\n" +
                        "Continue grabbing, moving, and looking around —\n" +
                        "but try to stay mentally relaxed. There is no task.\n\n" +
                        "This is the final calibration step (" +
                        activeBaselineDuration.ToString("F0") + " s).\n" +
                        "The system is recording your active-VR brain activity.";
                break;

            case Phase.Complete:
                title = "Ready!";
                body  = "Calibration complete.\n\n" +
                        "The puzzle room is loading. Good luck!";
                if (progressBar != null) { progressBar.value = 1f; }
                break;

            default:
                title = ""; body = ""; break;
        }

        if (instructionTitle != null) instructionTitle.text = title;
        if (instructionBody  != null) instructionBody.text  = body;
    }

    private void UpdateProgress(float value, string subtitle)
    {
        if (progressBar    != null) progressBar.value      = value;
        if (instructionBody != null && _phase == Phase.RestBaseline ||
            _phase == Phase.ActiveBaseline)
        {
            // Append live progress to body text
        }
        // Optionally update a subtitle label here if you add one to the scene
    }

    private void SetTutorialObjectsActive(bool active)
    {
        foreach (var go in tutorialGrabObjects)
            if (go != null) go.SetActive(active);
    }
}
