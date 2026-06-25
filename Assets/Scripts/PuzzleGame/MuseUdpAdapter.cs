using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// Receives the cognitive-load / stress signal from the external Python bridge
/// (Tools/muse_bridge.py) over local UDP and feeds it into CognitiveLoadAdapter.
///
/// WHY THIS EXISTS
///   BrainFlow cannot stream the Muse S Athena on Linux (it connects but fails to
///   subscribe to the data characteristics). The Python bridge talks to the headset
///   via SimpleBLE, does the band-power / baseline / stress maths, and emits one JSON
///   object per update over UDP. This component is the Unity end of that pipe and
///   replaces MuseAthenaAdapter as the data source. Everything downstream
///   (PieceHintSystem, SustainedStressDetector, AdaptiveDifficultyController) is
///   unchanged because they all listen to CognitiveLoadAdapter.
///
/// SETUP
///   1. Run on the same machine:  python Tools/muse_bridge.py --mac <MAC>
///   2. Add this component to a persistent GameObject; it survives scene loads and
///      pushes readings into whichever CognitiveLoadAdapter is active.
///   The Python baseline phase sends stress=0.5, so the game stays neutral until the
///   wearer's baseline is captured, then live values flow.
public class MuseUdpAdapter : MonoBehaviour, IMuseBaselineControl
{
    public static MuseUdpAdapter Instance { get; private set; }

    // Baseline-control command names — must match the Python bridge's run_session_unity.
    public const string CmdRestStart   = "baseline_rest_start";
    public const string CmdRestStop    = "baseline_rest_stop";
    public const string CmdActiveStart = "baseline_active_start";
    public const string CmdActiveStop  = "baseline_active_stop";

    [Header("UDP")]
    [Tooltip("Port the Python bridge sends to (muse_bridge.py --udp-port). Default 5005.")]
    public int port = 5005;

    [Header("Control channel (to the bridge's --unity baseline state machine)")]
    [Tooltip("Host the Python bridge runs on (usually localhost).")]
    public string bridgeHost = "127.0.0.1";
    [Tooltip("Port the bridge listens on for baseline commands (--control-port). Default 5006.")]
    public int controlPort = 5006;

    [Header("Optional explicit target (else CognitiveLoadAdapter.Instance is used)")]
    public CognitiveLoadAdapter cognitiveLoad;

    [Header("Live readings (read-only)")]
    [SerializeField] private string _status = "not started";
    [SerializeField, Range(0f, 1f)] private float _stress = 0.5f;
    [SerializeField] private string _phase = "—";
    [SerializeField] private bool   _contact = true;
    [SerializeField] private float  _secondsSinceLastPacket;

    public float  StressLevel => _stress;
    public string Phase       => _phase;
    public bool   Receiving   => _secondsSinceLastPacket < 5f;
    public string Status      => _status;
    
    public float ThetaZ { get; private set; }
    public float AlphaZ { get; private set; }
    public float Cli { get; private set; }

    private UdpClient _udp;
    private UdpClient _ctrlSender;
    private Thread _thread;
    private volatile bool _running;
    private readonly ConcurrentQueue<Reading> _queue = new ConcurrentQueue<Reading>();
    private float _lastPacketTime;

    // Command acknowledgements from the bridge.
    private Thread _ackThread;
    private volatile bool _ackRunning;
    private readonly ConcurrentQueue<string> _ackQueue = new ConcurrentQueue<string>();
    private readonly HashSet<string> _ackedCommands = new HashSet<string>();   // main-thread only

    /// Sends a baseline command to the bridge's --unity state machine, e.g.
    /// "baseline_rest_start", "baseline_rest_stop", "baseline_active_start",
    /// "baseline_active_stop", "reset". No-op friendly: if the bridge isn't running
    /// the datagram is simply dropped.
    public void SendCommand(string cmd)
    {
        try
        {
            EnsureCtrlSender();
            _ackedCommands.Remove(cmd);   // mark this command pending until (re)acked
            byte[] payload = Encoding.UTF8.GetBytes("{\"cmd\":\"" + cmd + "\"}\n");
            _ctrlSender.Send(payload, payload.Length, bridgeHost, controlPort);
            Debug.Log($"[MuseUdpAdapter] control -> {cmd}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MuseUdpAdapter] control send failed: {e.Message}");
        }
    }

    /// True (and consumes it) if the bridge has acknowledged <paramref name="cmd"/> since it
    /// was last sent. A caller polls this each frame until it returns true or a timeout elapses,
    /// so the on-screen baseline countdown only starts once the bridge has entered that phase.
    public bool ConsumeAck(string cmd) => _ackedCommands.Remove(cmd);

    private void EnsureCtrlSender()
    {
        if (_ctrlSender != null) return;
        _ctrlSender = new UdpClient();
        _ackRunning = true;
        _ackThread  = new Thread(AckReceiveLoop) { IsBackground = true, Name = "MuseUdpAck" };
        _ackThread.Start();
    }

    /// Background reader for the bridge's command acknowledgements. The bridge replies on the
    /// same socket we send commands from, so this receives on _ctrlSender and queues acks for
    /// the main thread to fold into _ackedCommands.
    private void AckReceiveLoop()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (_ackRunning)
        {
            try
            {
                byte[] bytes = _ctrlSender.Receive(ref remote);
                foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
                {
                    string s = line.Trim();
                    if (s.Length == 0) continue;
                    AckMsg a = JsonUtility.FromJson<AckMsg>(s);
                    if (!string.IsNullOrEmpty(a.ack)) _ackQueue.Enqueue(a.ack);
                }
            }
            catch (SocketException) { /* socket closed on stop */ }
            catch (Exception e) { Debug.LogWarning($"[MuseUdpAdapter] ack parse: {e.Message}"); }
        }
    }

    // ── IMuseBaselineControl — forwarded to the Python bridge over UDP ─────────
    public void StartRestBaseline()   => SendCommand("baseline_rest_start");
    public void StopRestBaseline()    => SendCommand("baseline_rest_stop");
    public void StartActiveBaseline() => SendCommand("baseline_active_start");
    public void FinalizeBaseline()    => SendCommand("baseline_active_stop");
    public void ResetBaseline()       => SendCommand("reset");

    [Serializable]
    private struct Reading
    {
        public string phase;
        public float stress;
        public float theta_z;
        public float alpha_z;
        public float cli;
        public bool contact;
        // 'bands' and 'progress' are intentionally omitted — JsonUtility ignores
        // unknown JSON fields, so we only declare what we consume.
    }

    [Serializable]
    private struct AckMsg { public string ack; }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()  => StartListener();
    private void OnDisable() => StopListener();

    private void StartListener()
    {
        if (_running) return;
        try
        {
            _udp = new UdpClient(port);
            _running = true;
            _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "MuseUdp" };
            _thread.Start();
            _status = $"listening on udp:{port}";
            Debug.Log($"[MuseUdpAdapter] {_status}");
        }
        catch (Exception e)
        {
            _status = $"bind failed: {e.Message}";
            Debug.LogError($"[MuseUdpAdapter] {_status}");
        }
    }

    private void ReceiveLoop()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                byte[] bytes = _udp.Receive(ref remote);
                
                // Auto-detect the Mac's IP address so control commands can reach it
                if (remote.Address != null)
                {
                    bridgeHost = remote.Address.ToString();
                }

                // The bridge may pack one JSON object per line; handle either.
                foreach (var line in Encoding.UTF8.GetString(bytes)
                             .Split('\n'))
                {
                    string s = line.Trim();
                    if (s.Length == 0) continue;
                    Reading r = JsonUtility.FromJson<Reading>(s);
                    _queue.Enqueue(r);
                }
            }
            catch (SocketException) { /* socket closed on stop */ }
            catch (Exception e) { Debug.LogWarning($"[MuseUdpAdapter] parse: {e.Message}"); }
        }
    }

    private void Update()
    {
        // Fold any acknowledgements the receive thread captured into the main-thread set.
        while (_ackQueue.TryDequeue(out string acked)) _ackedCommands.Add(acked);

        bool got = false;
        while (_queue.TryDequeue(out Reading r))
        {
            got = true;
            _stress  = Mathf.Clamp01(r.stress);
            _phase   = string.IsNullOrEmpty(r.phase) ? _phase : r.phase;
            _contact = r.contact;
            
            ThetaZ = r.theta_z;
            AlphaZ = r.alpha_z;
            Cli = r.cli;
        }

        if (got)
        {
            _lastPacketTime = Time.time;
            var target = cognitiveLoad != null ? cognitiveLoad : CognitiveLoadAdapter.Instance;
            if (target != null) target.SetStressLevel(_stress);
        }

        _secondsSinceLastPacket = Time.time - _lastPacketTime;
        if (_running)
            _status = Receiving ? $"streaming ({_phase})" : $"listening on udp:{port} (no data)";
    }

    private void StopListener()
    {
        _running    = false;
        _ackRunning = false;
        try { _udp?.Close(); } catch { }
        try { _thread?.Join(200); } catch { }
        try { _ctrlSender?.Close(); } catch { }
        try { _ackThread?.Join(200); } catch { }
        _udp = null;
        _thread = null;
        _ctrlSender = null;
        _ackThread = null;
        _status = "stopped";
    }

    private void OnDestroy() => StopListener();
}
