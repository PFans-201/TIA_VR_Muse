// ── BrainFlow compile guard ───────────────────────────────────────────────────
//
// This file compiles cleanly with OR without the BrainFlow DLL installed.
// When BrainFlow is NOT present the component is a harmless no-op stub that
// shows a setup reminder in the Inspector.  Once you install BrainFlow:
//
//   1. Add brainflow.dll + native .so/.dll/.dylib files to Assets/Plugins/.
//      (Full instructions below.)
//   2. Edit → Project Settings → Player → Scripting Define Symbols.
//   3. Add  MUSE_BRAINFLOW  and click Apply.
//   4. Full streaming code activates on the next compile.
//
// ── PLUGIN SETUP (one-time) ───────────────────────────────────────────────────
//
// a) Download the "brainflow" NuGet package (search nuget.org).
//    Rename .nupkg → .zip and extract.
//
// b) Copy managed assembly:
//    lib/netstandard2.0/brainflow.dll  →  Assets/Plugins/brainflow.dll
//
// c) Copy native libraries for your OS:
//    Linux   → Assets/Plugins/Linux/x86_64/
//               libBoardController.so  libDataHandler.so
//               libMLModule.so         libSimpleBleClient.so
//    Windows → Assets/Plugins/x86_64/
//               BoardController.dll  DataHandler.dll  MLModule.dll  SimpleBleClient.dll
//    macOS   → Assets/Plugins/macOS/
//               libBoardController.dylib  libDataHandler.dylib
//               libMLModule.dylib         libSimpleBleClient.dylib
//
//    Pre-built binaries: NuGet package build/<OS>/ folder, or brainflow GitHub Releases.
//
// d) Find your Muse S Athena MAC address (Linux):
//    $ bluetoothctl
//    [bluetoothctl] scan on          ← look for "Muse-XXXX", copy XX:XX:XX:XX:XX:XX
//    [bluetoothctl] scan off
// ─────────────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;

#if MUSE_BRAINFLOW
using System.Collections.Generic;
using System.Threading;
using brainflow;
using brainflow.math;
#endif

/// Real-time Muse S Athena EEG adapter with per-session baseline correction.
///
/// Signal pipeline (per 2-second window):
///   1. Band-pass filter 1–50 Hz (removes DC drift and powerline harmonics)
///   2. Compute five band powers: delta, theta, alpha, beta, gamma
///   3. During BASELINE phase (configurable seconds, default 60):
///      Record band-power statistics (mean + std) per channel — this is the
///      individual resting brain state for this session.
///   4. During ACTIVE phase:
///      Compute z-scored deviation from baseline for the two cognitive-load
///      indicators that neuroscience literature supports:
///        • Frontal theta ↑   (4–8 Hz)  — working memory / cognitive load
///        • Parietal alpha ↓  (8–13 Hz) — relaxation suppressed under load
///      Combine into a single cognitive-load index, map to [0, 1], smooth.
///
/// Pushes the final stress level to CognitiveLoadAdapter.SetStressLevel().
///
/// WHY NOT β/α RATIO (colleagues' approach)?
///   Beta/alpha tracks focused *attention* but not *overload*. Under cognitive
///   overload theta rises and alpha falls — the theta/alpha ratio is the
///   established cognitive-load index (Klimesch 1999, Onton et al. 2005).
///   β/α can be added as an additional signal but should not be the sole metric.
[DefaultExecutionOrder(-10)]
public class MuseAthenaAdapter : MonoBehaviour
{
#if MUSE_BRAINFLOW
    private const int k_BoardId = (int)BoardIds.MUSE_S_ATHENA_BOARD;  // 67
#else
    private const int k_BoardId = 67;
#endif

    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Device")]
    [Tooltip("Bluetooth MAC address of the Muse S Athena (e.g. D4:22:CD:00:AA:BB).\n" +
             "Required on Linux. Leave blank on macOS/Windows for auto-discovery.")]
    public string macAddress = "";

    [Header("Baseline Calibration")]
    [Tooltip("Seconds of resting EEG to record at the start of each session.\n" +
             "The player should sit still with eyes open and NOT be doing any task.\n" +
             "Their individual band-power mean + std is stored and used for\n" +
             "z-score normalisation during the active phase. 60 s is the minimum;\n" +
             "90 s is recommended for reliable statistics.")]
    [Range(30f, 180f)]
    public float baselineDurationSeconds = 60f;

    [Tooltip("Stress z-score at which the output reaches 0.5 (midpoint).\n" +
             "Increase this to require a stronger deviation from baseline\n" +
             "before the system considers the player stressed.")]
    [Range(0.5f, 4f)]
    public float stressSensitivity = 1.5f;

    [Header("Streaming")]
    [Tooltip("Seconds between stress-level updates sent to CognitiveLoadAdapter.")]
    [Range(1f, 10f)]
    public float updateIntervalSeconds = 2f;

    [Tooltip("Seconds of EEG history per band-power window.\n" +
             "Longer = more stable estimate; shorter = faster reaction.")]
    [Range(2f, 10f)]
    public float windowSeconds = 4f;

    [Tooltip("Exponential smoothing factor α (0 = frozen, 1 = no smoothing).\n" +
             "0.25 gives a ~4-step time constant — fast enough to track real changes\n" +
             "without jittering on single noisy windows.")]
    [Range(0f, 1f)]
    public float smoothingAlpha = 0.25f;

    [Tooltip("Also stream the ancillary preset (infrared optical / PPG channels).")]
    public bool enableOptical = true;

    [Header("References")]
    public CognitiveLoadAdapter cognitiveLoad;

    // ── Read-only debug display ───────────────────────────────────────────────

    [Header("Live Readings (Play Mode — read-only)")]
    [SerializeField] private string _connectionStatus = "Idle";
    [SerializeField] private string _phase            = "—";
    [SerializeField] private float  _baselineProgress;   // 0–1 during calibration
    [SerializeField] private float  _thetaZScore;        // raw z-score before combination
    [SerializeField] private float  _alphaZScore;
    [SerializeField] private float  _cognitiveLoadIndex; // combined, z-scored
    [SerializeField] private float  _stressLevel;        // final 0–1 output
    [SerializeField] private float  _opticalMagnitude;   // infrared PPG magnitude

#if MUSE_BRAINFLOW

    // ── Thread communication ──────────────────────────────────────────────────

    // Written by BG thread, read by Update() on main thread.
    private volatile float  _pendingStress   = -1f;   // -1 = no new value
    private volatile float  _pendingTheta    =  0f;
    private volatile float  _pendingAlpha    =  0f;
    private volatile float  _pendingCLI      =  0f;
    private volatile float  _pendingOptical  =  0f;
    private volatile float  _pendingProgress =  0f;
    private volatile string _pendingPhase    = "—";
    private volatile bool   _running;
    private Thread           _bgThread;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Start()
    {
        if (cognitiveLoad == null)
        {
            Debug.LogError("[MuseAthenaAdapter] cognitiveLoad not assigned. " +
                           "Use CognitiveLoadAdapter's debugStressOverride slider until the device is ready.");
            enabled = false;
            return;
        }
        _running  = true;
        _bgThread = new Thread(StreamLoop) { IsBackground = true, Name = "MuseAthena-BF" };
        _bgThread.Start();
    }

    private void Update()
    {
        float s = _pendingStress;
        if (s < 0f)
        {
            // Still update calibration progress display even before first stress value
            _baselineProgress = _pendingProgress;
            _phase            = _pendingPhase;
            return;
        }
        _pendingStress    = -1f;
        _stressLevel      = s;
        _thetaZScore      = _pendingTheta;
        _alphaZScore      = _pendingAlpha;
        _cognitiveLoadIndex = _pendingCLI;
        _opticalMagnitude = _pendingOptical;
        _baselineProgress = _pendingProgress;
        _phase            = _pendingPhase;
        cognitiveLoad.SetStressLevel(s);
    }

    private void OnDestroy()
    {
        _running = false;
        _bgThread?.Join(3000);
    }

    // ── Background streaming loop ─────────────────────────────────────────────

    private void StreamLoop()
    {
        var inputParams = new BrainFlowInputParams();
        if (!string.IsNullOrEmpty(macAddress))
            inputParams.mac_address = macAddress;

        var board     = new BoardShim(k_BoardId, inputParams);
        MLModel mindModel = null;

        try
        {
            BoardShim.disable_board_logger();
            SetStatus("Connecting...");
            board.prepare_session();
            board.start_stream(45000);
            SetStatus("Connected");

            int   samplingRate  = BoardShim.get_sampling_rate(k_BoardId);
            int[] eegChannels   = BoardShim.get_eeg_channels(k_BoardId);
            int   samplesNeeded = (int)(samplingRate * windowSeconds);

            int   ancPreset   = (int)BrainFlowPresets.ANCILLARY_PRESET;
            int   ancRate     = BoardShim.get_sampling_rate(k_BoardId, ancPreset);
            int[] optChannels = TryGetOpticalChannels(k_BoardId, ancPreset);

            // BrainFlow's built-in MINDFULNESS classifier as a secondary cross-check
            mindModel = new MLModel(new BrainFlowModelParams(
                (int)BrainFlowMetrics.MINDFULNESS, (int)BrainFlowClassifiers.DEFAULT_CLASSIFIER));
            mindModel.prepare();

            Debug.Log($"[MuseAthenaAdapter] Streaming — EEG {samplingRate} Hz, " +
                      $"optical {ancRate} Hz ({optChannels.Length} ch), " +
                      $"baseline {baselineDurationSeconds}s");

            // ── Phase 1: Baseline calibration ─────────────────────────────
            // Collect resting band powers to establish the individual reference.
            // Tell the player to sit still — this fires on the first frame.
            _pendingPhase = $"BASELINE — sit still, eyes open ({baselineDurationSeconds:F0}s)";

            var baselineSamples = new List<double[]>();   // each entry = [δ,θ,α,β,γ] averaged across channels
            double baselineStartTime = GetTimestamp();

            while (_running)
            {
                Thread.Sleep((int)(updateIntervalSeconds * 1000));
                if (!_running) break;

                double[,] raw = board.get_current_board_data(samplesNeeded);
                if (raw.GetLength(1) < samplesNeeded / 2) continue;

                double[] bp = GetFilteredBandPowers(raw, eegChannels, samplingRate);
                if (bp == null) continue;

                baselineSamples.Add(bp);

                double elapsed = GetTimestamp() - baselineStartTime;
                _pendingProgress = (float)Math.Min(1.0, elapsed / baselineDurationSeconds);

                if (elapsed >= baselineDurationSeconds) break;
            }
            if (!_running) return;

            // Compute baseline mean and std for each band
            int nBands = 5;  // [delta, theta, alpha, beta, gamma]
            double[] baselineMean = new double[nBands];
            double[] baselineStd  = new double[nBands];
            ComputeMeanStd(baselineSamples, baselineMean, baselineStd);

            Debug.Log($"[MuseAthenaAdapter] Baseline complete ({baselineSamples.Count} windows). " +
                      $"θ mean={baselineMean[1]:F3} std={baselineStd[1]:F3}  " +
                      $"α mean={baselineMean[2]:F3} std={baselineStd[2]:F3}");

            // ── Phase 2: Active monitoring ─────────────────────────────────
            _pendingPhase    = "ACTIVE";
            _pendingProgress = 1f;
            float smoothedStress = 0.5f;

            while (_running)
            {
                Thread.Sleep((int)(updateIntervalSeconds * 1000));
                if (!_running) break;

                // ── EEG: baseline-corrected cognitive load index ───────────
                double[,] eegData = board.get_current_board_data(samplesNeeded);
                if (eegData.GetLength(1) < samplesNeeded / 2) continue;

                double[] bp = GetFilteredBandPowers(eegData, eegChannels, samplingRate);
                if (bp == null) continue;

                // θ z-score (positive = more theta than at rest → more load)
                double thetaZ = ZScore(bp[1], baselineMean[1], baselineStd[1]);
                // α z-score  (negative = alpha suppressed relative to rest → more load)
                double alphaZ = ZScore(bp[2], baselineMean[2], baselineStd[2]);

                // Cognitive load index: theta up AND alpha down both indicate load.
                // We invert alphaZ so both contribute positively when load is high.
                double cli = (thetaZ - alphaZ) / 2.0;

                // Map cli to [0,1] with stressSensitivity controlling the midpoint.
                // sigmoid(cli / sensitivity):  at cli=sensitivity → output ≈ 0.73
                double rawStress = 1.0 / (1.0 + Math.Exp(-cli / stressSensitivity));

                // Exponential moving average smoothing
                smoothedStress = smoothingAlpha * (float)rawStress +
                                 (1f - smoothingAlpha) * smoothedStress;

                _pendingTheta  = (float)thetaZ;
                _pendingAlpha  = (float)alphaZ;
                _pendingCLI    = (float)cli;
                _pendingStress = Mathf.Clamp01(smoothedStress);

                // ── Optical (infrared PPG) magnitude ──────────────────────
                if (enableOptical && optChannels.Length > 0)
                {
                    try
                    {
                        double[,] anc = board.get_current_board_data(ancRate * 4, ancPreset);
                        int n = anc.GetLength(1);
                        if (n > 0)
                        {
                            double sum = 0;
                            int ch = optChannels[0];
                            for (int i = 0; i < n; i++) sum += Math.Abs(anc[ch, i]);
                            _pendingOptical = (float)(sum / n);
                        }
                    }
                    catch { /* ancillary not yet populated — skip */ }
                }
            }
        }
        catch (BrainFlowError e)
        {
            string msg = $"BrainFlow {e.exit_code}: {e.Message}";
            SetStatus(msg);
            Debug.LogError($"[MuseAthenaAdapter] {msg}\n" +
                           "Check: bluetooth running? correct MAC? native libs in Assets/Plugins/?");
        }
        catch (Exception e)
        {
            SetStatus($"Error: {e.Message}");
            Debug.LogError($"[MuseAthenaAdapter] {e}");
        }
        finally
        {
            try { mindModel?.release();    } catch { }
            try { board.stop_stream();     } catch { }
            try { board.release_session(); } catch { }
            SetStatus("Disconnected");
        }
    }

    // ── Signal processing helpers ─────────────────────────────────────────────

    /// Returns [delta, theta, alpha, beta, gamma] average band powers across all
    /// EEG channels, after band-pass filtering 1–50 Hz.
    /// Returns null if the data contains NaN/flat signal (poor contact).
    private static double[] GetFilteredBandPowers(double[,] data, int[] channels, int samplingRate)
    {
        // Quality check: reject if signal is flat or saturated
        int cols = data.GetLength(1);
        double sumSq = 0;
        foreach (int ch in channels)
            for (int i = 0; i < cols; i++) sumSq += data[ch, i] * data[ch, i];
        double rms = Math.Sqrt(sumSq / (channels.Length * cols));
        if (rms < 0.5 || rms > 800.0) return null;

        // Band-pass 1–50 Hz to remove DC drift and powerline noise before FFT
        double[,] filtered = (double[,])data.Clone();
        foreach (int ch in channels)
        {
            double[] col = new double[cols];
            for (int i = 0; i < cols; i++) col[i] = filtered[ch, i];
            DataFilter.perform_bandpass(col, samplingRate, 1.0, 50.0, 4,
                (int)FilterTypes.BUTTERWORTH, 0.0);
            for (int i = 0; i < cols; i++) filtered[ch, i] = col[i];
        }

        var bands = DataFilter.get_avg_band_powers(filtered, channels, samplingRate, false);
        double[] bp = bands.Item1;  // [delta, theta, alpha, beta, gamma]

        foreach (double v in bp)
            if (double.IsNaN(v) || double.IsInfinity(v)) return null;

        return bp;
    }

    private static double ZScore(double value, double mean, double std)
        => std > 1e-9 ? (value - mean) / std : 0.0;

    private static void ComputeMeanStd(List<double[]> samples, double[] mean, double[] std)
    {
        int n = samples.Count;
        if (n == 0) return;
        int nBands = mean.Length;

        for (int b = 0; b < nBands; b++)
        {
            double sum = 0;
            foreach (var s in samples) sum += s[b];
            mean[b] = sum / n;

            double variance = 0;
            foreach (var s in samples) variance += (s[b] - mean[b]) * (s[b] - mean[b]);
            std[b] = Math.Sqrt(variance / n);
        }
    }

    private static double GetTimestamp() =>
        (double)System.Diagnostics.Stopwatch.GetTimestamp() /
        System.Diagnostics.Stopwatch.Frequency;

    private static int[] TryGetOpticalChannels(int boardId, int preset)
    {
        try   { return BoardShim.get_optical_channels(boardId, preset); }
        catch { return Array.Empty<int>(); }
    }

#else   // ── Stub when MUSE_BRAINFLOW is not defined ──────────────────────────

    private void Start()
    {
        SetStatus("BrainFlow not installed — see file header for setup instructions. " +
                  "Use CognitiveLoadAdapter.debugStressOverride to simulate readings.");
        Debug.LogWarning("[MuseAthenaAdapter] MUSE_BRAINFLOW not defined. " +
                         "Add it to Scripting Define Symbols after installing the BrainFlow plugin.");
    }

#endif  // MUSE_BRAINFLOW

    private void SetStatus(string msg) => _connectionStatus = msg;
}
