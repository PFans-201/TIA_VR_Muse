using System;
using System.Collections;
using System.Text;
using UnityEngine;

/// Standalone (no-PC) Muse S Athena adapter for the Meta Quest: connects to the
/// headset over BLE on-device, decodes the EEG itself, and runs the validated
/// MuseSignalProcessor — producing the same stress signal as the Python bridge but
/// entirely inside the headset.
///
/// It exposes the SAME surface as MuseUdpAdapter (StressLevel / Phase, feeds
/// CognitiveLoadAdapter) and the SAME baseline-control methods, so the rest of the
/// game and TutorialManager don't care which adapter is in use.
///
/// BLE is abstracted behind IMuseBleTransport — add a component implementing it
/// (e.g. a Velorexe wrapper, see docs/muse-unity-bridge.md) to the same GameObject.
public class MuseDirectAdapter : MonoBehaviour, IMuseBaselineControl
{
    public static MuseDirectAdapter Instance { get; private set; }

    // ── Muse GATT (model-specific, OS-independent) ────────────────────────────
    private const string Service = "0000fe8d-0000-1000-8000-00805f9b34fb";
    private const string Ctrl    = "273e0001-4c4d-454d-96be-f03bac821358";
    private static readonly string[] EegChars =
    {
        "273e0003-4c4d-454d-96be-f03bac821358", // 0 TP9
        "273e0004-4c4d-454d-96be-f03bac821358", // 1 AF7
        "273e0005-4c4d-454d-96be-f03bac821358", // 2 AF8
        "273e0006-4c4d-454d-96be-f03bac821358", // 3 TP10
    };
    private static readonly string[] StartCmds = { "h", "p1041", "s", "d" };

    [Header("Device")]
    [Tooltip("Substring of the BLE name to connect to (e.g. \"Muse\" or \"MuseS-9A06\").")]
    public string deviceNameContains = "Muse";

    [Header("Tuning")]
    [Tooltip("Seconds between stress updates (lower = lower latency).")]
    public float updateInterval = 1f;
    [Range(0.5f, 4f)] public float sensitivity = 1.5f;

    [Header("Optional explicit target (else CognitiveLoadAdapter.Instance is used)")]
    public CognitiveLoadAdapter cognitiveLoad;

    [Header("Live (read-only)")]
    [SerializeField] private string _status = "not started";
    [SerializeField, Range(0f, 1f)] private float _stress = 0.5f;
    [SerializeField] private string _phase = "idle";
    [SerializeField] private bool _contact;

    public float  StressLevel => _stress;
    public string Phase       => _phase;
    public bool   Contact     => _contact;
    public string Status      => _status;

    // Latest signal-processor reading — updated every updateInterval seconds.
    // Safe to read from the main thread (written only in Update).
    public MuseSignalProcessor.Reading LastReading { get; private set; }

    private IMuseBleTransport _ble;
    private readonly MuseSignalProcessor _proc = new MuseSignalProcessor();
    private volatile bool _connected;
    private volatile bool _streaming;   // written from OnDisconnected (may be BLE thread)
    private volatile bool _settingUp;   // written from OnDisconnected (may be BLE thread)
    private float _tickAccum;
    private Coroutine _setupCoroutine;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        _proc.Sensitivity = sensitivity;
        _ble = GetComponent<IMuseBleTransport>();
    }

    private void Start()
    {
        if (_ble == null)
        {
            _status = "no IMuseBleTransport component — add a BLE plugin wrapper";
            Debug.LogError("[MuseDirectAdapter] " + _status);
            return;
        }
        _status = "scanning";
        _ble.StartScan(deviceNameContains, OnDeviceFound);
    }

    // ── BLE callbacks ─────────────────────────────────────────────────────────
    private void OnDeviceFound(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            Debug.LogError("[MuseDirectAdapter] OnDeviceFound received null/empty deviceId — ignoring.");
            return;
        }
        _status = $"connecting to {deviceId}";
        _ble.Connect(deviceId, () => _connected = true, OnDisconnected);
    }

    private void OnDisconnected()
    {
        _connected = false;
        _streaming = false;
        _settingUp = false;   // volatile — visible to main-thread Update immediately
        _status = "disconnected — rescanning...";
        Debug.LogWarning("[MuseDirectAdapter] Disconnected from Muse. Will rescan.");

        // Cancel any in-progress SetupStream coroutine so it doesn't issue
        // duplicate Subscribe/Write commands when we reconnect.
        if (_setupCoroutine != null) { StopCoroutine(_setupCoroutine); _setupCoroutine = null; }

        // Restart scan so the headset can reconnect after a dropout.
        if (_ble != null && gameObject.activeInHierarchy)
        {
            _status = "rescanning after disconnect";
            _ble.StartScan(deviceNameContains, OnDeviceFound);
        }
    }

    private void OnEegData(int channel, byte[] payload)
    {
        if (payload == null || payload.Length < 20) return;
        _proc.PushSamples(channel, MuseSignalProcessor.DecodePacket(payload));
    }

    // Subscribe + start the stream on the main thread once connected.
    // A short delay after connect lets the Quest's GATT stack finish service
    // discovery before we subscribe to characteristics.
    private IEnumerator SetupStream()
    {
        _status = "connected — waiting for GATT service discovery...";
        Debug.Log("[MuseDirectAdapter] Waiting for GATT service discovery (0.8s).");
        yield return new WaitForSeconds(0.8f);

        if (!_connected) { _settingUp = false; yield break; }  // disconnected while waiting

        _status = "subscribing to EEG characteristics...";
        for (int c = 0; c < EegChars.Length; c++)
        {
            int ch = c;   // capture
            Debug.Log($"[MuseDirectAdapter] Subscribing channel {ch}: {EegChars[c]}");
            _ble.Subscribe(Service, EegChars[c], data => OnEegData(ch, data));
            yield return new WaitForSeconds(0.3f);  // give each subscription time to confirm
        }

        _status = "sending start commands...";
        foreach (var cmd in StartCmds)
        {
            Debug.Log($"[MuseDirectAdapter] Sending command: {cmd}");
            _ble.WriteCommand(Service, Ctrl, EncodeCommand(cmd));
            yield return new WaitForSeconds(0.3f);
        }

        _streaming = true;
        _settingUp = false;
        _status = "streaming";
        Debug.Log("[MuseDirectAdapter] EEG streaming started.");
    }

    private static byte[] EncodeCommand(string s)
    {
        byte[] body = Encoding.ASCII.GetBytes(s);
        var packet = new byte[body.Length + 2];
        packet[0] = (byte)(body.Length + 1);
        Array.Copy(body, 0, packet, 1, body.Length);
        packet[body.Length + 1] = (byte)'\n';
        return packet;
    }

    // ── Per-frame ─────────────────────────────────────────────────────────────
    private void Update()
    {
        if (_connected && !_streaming && !_settingUp)
        {
            _settingUp = true;
            // Cancel any stale coroutine (safety net for rapid disconnect/reconnect)
            if (_setupCoroutine != null) StopCoroutine(_setupCoroutine);
            _setupCoroutine = StartCoroutine(SetupStream());
        }
        if (!_streaming) return;

        _tickAccum += Time.deltaTime;
        if (_tickAccum < updateInterval) return;
        _tickAccum = 0f;

        _proc.Sensitivity = sensitivity;
        MuseSignalProcessor.Reading r = _proc.Tick();
        LastReading = r;
        _stress  = r.stress;
        _phase   = r.phase.ToString();
        _contact = r.contact;
        _status  = $"streaming ({_phase})";

        var target = cognitiveLoad != null ? cognitiveLoad : CognitiveLoadAdapter.Instance;
        if (target != null) target.SetStressLevel(_stress);
    }

    // ── IMuseBaselineControl (called by TutorialManager) ──────────────────────
    public void StartRestBaseline()   => _proc.StartRestBaseline();
    public void StopRestBaseline()    => _proc.StopRestBaseline();
    public void StartActiveBaseline() => _proc.StartActiveBaseline();
    public void FinalizeBaseline()
    {
        if (!_proc.FinalizeBaseline())
            Debug.LogWarning("[MuseDirectAdapter] baseline finalize failed — no stable channel");
    }
    public void ResetBaseline()       => _proc.Reset();

    private void OnDestroy()
    {
        try { _ble?.Disconnect(); } catch { }
    }
}
