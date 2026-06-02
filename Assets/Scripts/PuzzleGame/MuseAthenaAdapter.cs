// ── BrainFlow compile guard ───────────────────────────────────────────────────
//
// This file compiles cleanly with OR without the BrainFlow DLL installed.
// When BrainFlow is NOT present the component is a harmless no-op stub that
// shows a setup reminder in the Inspector.  Once you install BrainFlow:
//
//   1. Add brainflow.dll + native .so/.dll/.dylib files to Assets/Plugins/.
//      (Instructions in the comment block below.)
//   2. Open Edit → Project Settings → Player → Scripting Define Symbols.
//   3. Add  MUSE_BRAINFLOW  and click Apply.
//   4. Full streaming code activates on the next compile.
//
// ── PLUGIN SETUP (one-time) ───────────────────────────────────────────────────
//
// a) Download the "brainflow" NuGet package from nuget.org.
//    Rename .nupkg → .zip and extract.
//
// b) Copy managed assembly:
//    lib/netstandard2.0/brainflow.dll   →   Assets/Plugins/brainflow.dll
//
// c) Copy native libraries for your OS:
//    Linux   → Assets/Plugins/Linux/x86_64/
//               libBoardController.so  libDataHandler.so
//               libMLModule.so         libSimpleBleClient.so
//    Windows → Assets/Plugins/x86_64/
//               BoardController.dll  DataHandler.dll
//               MLModule.dll         SimpleBleClient.dll
//    macOS   → Assets/Plugins/macOS/
//               libBoardController.dylib  libDataHandler.dylib
//               libMLModule.dylib         libSimpleBleClient.dylib
//
//    Pre-built binaries live in the NuGet package under build/<OS>/
//    or at github.com/brainflow-dev/brainflow/releases.
//
// d) Find your Muse S Athena MAC address (Linux):
//    $ bluetoothctl
//    [bluetoothctl] scan on
//    # Look for "Muse-XXXX" — copy its XX:XX:XX:XX:XX:XX address
//    [bluetoothctl] scan off
//    [bluetoothctl] exit
//
// ─────────────────────────────────────────────────────────────────────────────

using System;
using UnityEngine;

#if MUSE_BRAINFLOW
using System.Threading;
using brainflow;
using brainflow.math;
#endif

/// Real-time Muse S Athena EEG + optical adapter.
///
/// Streams EEG (256 Hz — TP9/AF7/AF8/TP10) and infrared optical PPG (64 Hz)
/// from the Muse S Athena via BrainFlow.  Computes a MINDFULNESS score every
/// <see cref="updateIntervalSeconds"/> seconds and pushes the derived stress
/// level to <see cref="CognitiveLoadAdapter.SetStressLevel"/> on the main thread.
///
/// Requires the MUSE_BRAINFLOW scripting define and the BrainFlow plugin files
/// in Assets/Plugins/.  See the comment block at the top of this file.
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

    [Header("Streaming")]
    [Tooltip("Seconds between stress level updates pushed to CognitiveLoadAdapter.")]
    [Range(1f, 10f)]
    public float updateIntervalSeconds = 2f;

    [Tooltip("Seconds of EEG history used per band-power computation.\n" +
             "Longer = more stable score; shorter = faster reaction to state changes.")]
    [Range(2f, 10f)]
    public float windowSeconds = 4f;

    [Tooltip("Also stream the ancillary preset (infrared optical / PPG).\n" +
             "Provides a heart-rate proxy reading in the Inspector.")]
    public bool enableOptical = true;

    [Header("References")]
    public CognitiveLoadAdapter cognitiveLoad;

    // ── Read-only debug display ───────────────────────────────────────────────

    [Header("Live Readings (Play Mode, read-only)")]
    [SerializeField] private string _connectionStatus = "Idle";
    [SerializeField] private float  _mindfulnessScore;
    [SerializeField] private float  _restfulnessScore;
    [SerializeField] private float  _stressLevel;
    [SerializeField] private float  _opticalMagnitude;  // raw PPG magnitude — not BPM

#if MUSE_BRAINFLOW

    // ── Thread-safe value exchange ────────────────────────────────────────────

    private volatile float _pendingStress   = -1f;  // -1 = no new value
    private volatile float _pendingMind     =  0f;
    private volatile float _pendingRest     =  0f;
    private volatile float _pendingOptical  =  0f;
    private volatile bool  _running;
    private Thread         _bgThread;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Start()
    {
        if (cognitiveLoad == null)
        {
            Debug.LogError("[MuseAthenaAdapter] cognitiveLoad not assigned. Disable this component " +
                           "and use CognitiveLoadAdapter's debugStressOverride slider instead.");
            enabled = false;
            return;
        }

        _running  = true;
        _bgThread = new Thread(StreamLoop)
        {
            IsBackground = true,
            Name         = "MuseAthena-BrainFlow"
        };
        _bgThread.Start();
    }

    private void Update()
    {
        float s = _pendingStress;
        if (s < 0f) return;

        _pendingStress    = -1f;
        _stressLevel      = s;
        _mindfulnessScore = _pendingMind;
        _restfulnessScore = _pendingRest;
        _opticalMagnitude = _pendingOptical;
        cognitiveLoad.SetStressLevel(s);
    }

    private void OnDestroy()
    {
        _running = false;
        _bgThread?.Join(3000);
    }

    // ── Background streaming thread ───────────────────────────────────────────

    private void StreamLoop()
    {
        var inputParams = new BrainFlowInputParams();
        if (!string.IsNullOrEmpty(macAddress))
            inputParams.mac_address = macAddress;

        var board = new BoardShim(k_BoardId, inputParams);

        MLModel mindModel = null;
        MLModel restModel = null;

        try
        {
            BoardShim.disable_board_logger();

            SetStatus("Connecting...");
            board.prepare_session();
            board.start_stream(45000);   // ring buffer ~2.9 min of EEG at 256 Hz
            SetStatus("Connected");

            int   samplingRate  = BoardShim.get_sampling_rate(k_BoardId);
            int[] eegChannels   = BoardShim.get_eeg_channels(k_BoardId);
            int   samplesNeeded = (int)(samplingRate * windowSeconds);

            int   ancPreset   = (int)BrainFlowPresets.ANCILLARY_PRESET;
            int   ancRate     = BoardShim.get_sampling_rate(k_BoardId, ancPreset);
            int[] optChannels = TryGetOpticalChannels(k_BoardId, ancPreset);

            Debug.Log($"[MuseAthenaAdapter] Connected — EEG {samplingRate} Hz (TP9/AF7/AF8/TP10), " +
                      $"optical {ancRate} Hz ({optChannels.Length} channels)");

            mindModel = new MLModel(new BrainFlowModelParams(
                (int)BrainFlowMetrics.MINDFULNESS, (int)BrainFlowClassifiers.DEFAULT_CLASSIFIER));
            mindModel.prepare();

            restModel = new MLModel(new BrainFlowModelParams(
                (int)BrainFlowMetrics.RESTFULNESS, (int)BrainFlowClassifiers.DEFAULT_CLASSIFIER));
            restModel.prepare();

            while (_running)
            {
                Thread.Sleep((int)(updateIntervalSeconds * 1000));
                if (!_running) break;

                // ── EEG → mindfulness → stress ────────────────────────────
                double[,] eegData = board.get_current_board_data(samplesNeeded);
                int cols = eegData.GetLength(1);

                if (cols < samplesNeeded / 2)
                {
                    SetStatus($"Buffering EEG... ({cols}/{samplesNeeded})");
                    continue;
                }

                var    bands = DataFilter.get_avg_band_powers(eegData, eegChannels, samplingRate, true);
                double mind  = mindModel.predict(bands.Item1)[0];
                double rest  = restModel.predict(bands.Item1)[0];

                _pendingMind   = (float)mind;
                _pendingRest   = (float)rest;
                _pendingStress = Mathf.Clamp01(1f - (float)mind);  // stress ≈ 1 − mindfulness

                // ── Ancillary optical / infrared PPG ──────────────────────
                if (enableOptical && optChannels.Length > 0)
                {
                    try
                    {
                        double[,] ancData = board.get_current_board_data(ancRate * 4, ancPreset);
                        int       n       = ancData.GetLength(1);
                        if (n > 0)
                        {
                            double sum = 0;
                            int    ch  = optChannels[0];  // first infrared LED
                            for (int i = 0; i < n; i++) sum += Math.Abs(ancData[ch, i]);
                            _pendingOptical = (float)(sum / n);
                        }
                    }
                    catch { /* ancillary stream not yet populated — skip silently */ }
                }
            }
        }
        catch (BrainFlowError e)
        {
            string msg = $"BrainFlow error {e.exit_code}: {e.Message}";
            SetStatus(msg);
            Debug.LogError($"[MuseAthenaAdapter] {msg}\n" +
                           "Check: bluetooth running? correct MAC? brainflow native libs in Assets/Plugins/?");
        }
        catch (Exception e)
        {
            SetStatus($"Error: {e.Message}");
            Debug.LogError($"[MuseAthenaAdapter] {e}");
        }
        finally
        {
            try { mindModel?.release();    } catch { }
            try { restModel?.release();    } catch { }
            try { board.stop_stream();     } catch { }
            try { board.release_session(); } catch { }
            SetStatus("Disconnected");
        }
    }

    private static int[] TryGetOpticalChannels(int boardId, int preset)
    {
        try   { return BoardShim.get_optical_channels(boardId, preset); }
        catch { return Array.Empty<int>(); }
    }

#else   // ── Stub when MUSE_BRAINFLOW is not defined ──────────────────────────

    private void Start()
    {
        SetStatus("BrainFlow not installed — see Assets/Scripts/PuzzleGame/MuseAthenaAdapter.cs " +
                  "header for setup instructions. Use CognitiveLoadAdapter debugStressOverride to test.");
        Debug.LogWarning("[MuseAthenaAdapter] BrainFlow plugin not found. " +
                         "Add MUSE_BRAINFLOW to Scripting Define Symbols after installing the plugin.");
    }

#endif  // MUSE_BRAINFLOW

    private void SetStatus(string msg) => _connectionStatus = msg;
}
