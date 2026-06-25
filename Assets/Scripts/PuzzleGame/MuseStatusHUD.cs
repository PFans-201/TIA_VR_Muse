using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// Self-contained Muse debug overlay for VR (Quest).
///
/// Drop on any GameObject in the scene (it creates its own World Space canvas
/// as a child of Camera.main at runtime, so no manual scene wiring needed).
///
/// Phase-aware — it shows different data while calibrating vs while playing:
///   • Connection status dot  (orange=scanning, yellow=connecting, green=streaming, red=error)
///   • During the BASELINES    raw band-power bars (δ θ α β γ) + signal-quality, since there are
///                             no standardized indices yet (it's still measuring the reference).
///   • During GAMEPLAY         the three standardized 0–1 indices: stress, cognitive load,
///                             attention (scrolling graph + numeric readout). Raw z-scores are
///                             intentionally not surfaced here.
///
/// Position: inspector field hudPosition (local camera coords, default bottom-right).
public class MuseStatusHUD : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("VR Position (camera-local metres)")]
    [Tooltip("Where to place the HUD relative to the headset camera. " +
             "X+ = right, Y- = down, Z+ = forward.")]
    public Vector3 hudPosition = new Vector3(0.30f, -0.20f, 0.65f);

    [Tooltip("World-scale per canvas pixel. 0.0008 ≈ 37 cm wide panel at 65 cm depth.")]
    public float hudScale = 0.0008f;

    [Header("Graph")]
    [Tooltip("How often to add a new sample to the graph (seconds).")]
    public float sampleInterval = 0.5f;

    // ── Canvas dimensions (pixels) ────────────────────────────────────────────
    private const int PanelW  = 470;
    private const int PanelH  = 210;
    private const int GraphW  = 454;
    private const int GraphH  = 90;

    // ── Ring buffers for the gameplay graph (the three standardized 0–1 indices) ──
    private readonly float[] _sBuf = new float[GraphW];  // stress
    private readonly float[] _cBuf = new float[GraphW];  // cognitive load
    private readonly float[] _aBuf = new float[GraphW];  // attention
    private int   _head;
    private float _sampleTimer;
    private bool  _hasData;
    private int   _buildRetries;
    private const int MaxBuildRetries = 10;  // 10 × 0.5s = 5s max wait for Camera.main

    // Baseline (calibration) state — no standardized indices yet, so we show live raw bands.
    private bool _baselineMode;
    private readonly float[] _bandsDisp = new float[5];   // δ θ α β γ raw band powers
    private static readonly string[] k_BandNames = { "δ", "θ", "α", "β", "γ" };

    // ── UI refs ───────────────────────────────────────────────────────────────
    private TextMeshProUGUI _statusLabel;
    private TextMeshProUGUI _valuesLabel;
    private TextMeshProUGUI _legendLabel;
    private RawImage        _graphImg;
    private Texture2D       _tex;

    // ── Palette ───────────────────────────────────────────────────────────────
    static readonly Color32 CBack   = new Color32( 10,  12,  20, 235);
    static readonly Color32 CGrid   = new Color32( 70,  75,  95,  45);
    static readonly Color32 CGreen  = new Color32( 48, 210,  88, 255);   // stress
    static readonly Color32 COrange = new Color32(255, 148,  38, 255);   // cognitive load
    static readonly Color32 CBlue   = new Color32( 72, 158, 255, 255);   // attention
    static readonly Color32 CBand   = new Color32(120, 200, 160, 255);   // baseline band bars

    const string HexScan  = "#FF8020";
    const string HexConn  = "#FFD91A";
    const string HexOK    = "#33E04D";
    const string HexErr   = "#FF3344";

    // ─────────────────────────────────────────────────────────────────────────
    private void Start()
    {
        // Texture for the graph
        _tex = new Texture2D(GraphW, GraphH, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Point };
        ClearTex();

        BuildHUD();
    }

    private void Update()
    {
        RefreshStatus();

        _sampleTimer += Time.deltaTime;
        if (_sampleTimer >= sampleInterval)
        {
            _sampleTimer = 0f;
            AddSample();
            RedrawGraph();
        }
    }

    // ── Status label ──────────────────────────────────────────────────────────
    void RefreshStatus()
    {
        if (_statusLabel == null) return;

        // Try direct BLE adapter first; fall back to UDP adapter (WiFi mode)
        var mda = MuseDirectAdapter.Instance;
        var udp = MuseUdpAdapter.Instance;
        if (mda == null && udp == null)
        {
            _statusLabel.text = $"<color={HexErr}>●</color>  No Muse adapter in scene";
            return;
        }

        // Prefer UDP if actively receiving, otherwise prefer Direct BLE, else UDP
        string status;
        float stressLevel = 0.5f;
        bool contact = false;
        bool useUdp = udp != null && (udp.Receiving || mda == null);

        if (useUdp)
        {
            status = udp.Status;
            stressLevel = udp.StressLevel;
            contact = udp.Receiving;
        }
        else if (mda != null)
        {
            status = mda.Status;
            stressLevel = mda.StressLevel;
            contact = mda.Contact;
        }
        else
        {
            status = "No adapter";
        }

        string hex;
        if      (status.Contains("streaming"))                                       hex = HexOK;
        else if (status.Contains("connecting") || status.Contains("subscribing") ||
                 status.Contains("sending")    || status.Contains("GATT") ||
                 status.Contains("commands"))                                         hex = HexConn;
        else if (status.Contains("scanning") || status.Contains("listening"))         hex = HexScan;
        else if (status.Contains("no ") || status.Contains("error") ||
                 status.Contains("Error") || status.Contains("failed"))              hex = HexErr;
        else                                                                         hex = HexScan;

        // While calibrating, don't show the placeholder stress (it's a constant 0.5) — say so.
        bool baseline = useUdp && udp.IsBaselinePhase;
        string extra;
        if (baseline)
            extra = $"   <color={HexConn}>calibrating baseline…</color>";
        else if (status.Contains("streaming"))
            extra = $"   contact: {(contact ? $"<color={HexOK}><b>✓</b></color>" : $"<color={HexErr}><b>✗</b></color>")}";
        else
            extra = string.Empty;

        _statusLabel.text = $"<color={hex}>●</color>  {status}{extra}";
    }

    // ── Graph sample ──────────────────────────────────────────────────────────
    void AddSample()
    {
        var mda = MuseDirectAdapter.Instance;
        var udp = MuseUdpAdapter.Instance;
        bool useUdp = udp != null && (udp.Receiving || mda == null);

        // ── Baseline calibration: no standardized indices yet → show live raw bands ──
        _baselineMode = useUdp && udp.IsBaselinePhase;
        if (_baselineMode)
        {
            var b = udp.Bands;
            for (int i = 0; i < 5; i++) _bandsDisp[i] = (b != null && i < b.Length) ? b[i] : 0f;
            _hasData = true;

            if (_valuesLabel != null)
            {
                bool contact = udp.Contact;
                _valuesLabel.text =
                    $"<color={HexConn}>calibrating…</color>  measuring your baseline    " +
                    $"signal: {(contact ? $"<color={HexOK}><b>good ✓</b></color>" : $"<color={HexErr}><b>poor ✗</b></color>")}";
            }
            if (_legendLabel != null)
                _legendLabel.text =
                    $"<color=#78C8A0>■</color> live brain-wave bands (δ θ α β γ) — stay still and relaxed";
            return;
        }

        // ── Gameplay streaming: the three standardized 0–1 indices ──
        float stress, cog, att;
        bool haveIndices;
        if (useUdp)
        {
            stress = udp.StressLevel; cog = udp.CognitiveLoad; att = udp.Attention; haveIndices = true;
        }
        else if (mda != null)
        {
            stress = mda.LastReading.stress; cog = 0f; att = 0f; haveIndices = false;   // on-device: stress only
        }
        else return;

        _sBuf[_head] = stress;
        _cBuf[_head] = cog;
        _aBuf[_head] = att;
        _head = (_head + 1) % GraphW;
        _hasData = true;

        if (_valuesLabel != null)
        {
            _valuesLabel.text = haveIndices
                ? $"<color={HexOK}>stress {stress:F2}</color>     " +
                  $"<color=#FF9426>cognitive load {cog:F2}</color>     " +
                  $"<color=#489EFF>attention {att:F2}</color>"
                : $"<color={HexOK}>stress {stress:F2}</color>     (on-device: stress only)";
        }
        if (_legendLabel != null)
            _legendLabel.text = haveIndices
                ? $"<color={HexOK}>■</color> Stress    <color=#FF9426>■</color> Cognitive load    " +
                  $"<color=#489EFF>■</color> Attention    grid = 30 s  (0–1)"
                : $"<color={HexOK}>■</color> Stress (0–1)    grid = 30 s";
    }

    // ── Graph redraw ──────────────────────────────────────────────────────────
    void RedrawGraph()
    {
        if (!_hasData) return;

        var px = _tex.GetPixels32();
        for (int i = 0; i < px.Length; i++) px[i] = CBack;

        if (_baselineMode) { RedrawBars(px); }
        else
        {
            // Horizontal grid lines at 0.25, 0.5, 0.75
            foreach (float f in new float[] { 0.25f, 0.5f, 0.75f })
            {
                int gy = Mathf.RoundToInt(f * (GraphH - 1));
                for (int x = 0; x < GraphW; x++) px[gy * GraphW + x] = CGrid;
            }
            // Vertical grid every 60 samples  (60 × 0.5 s = 30 s intervals)
            for (int x = 0; x < GraphW; x += 60)
                for (int y = 0; y < GraphH; y++) px[y * GraphW + x] = CGrid;

            // Plot the three indices (back to front so stress reads on top)
            PlotLine(px, _aBuf, CBlue);     // attention
            PlotLine(px, _cBuf, COrange);   // cognitive load
            PlotLine(px, _sBuf, CGreen);    // stress
        }

        _tex.SetPixels32(px);
        _tex.Apply();
    }

    // ── Baseline band bars ──────────────────────────────────────────────────────
    /// Draws the five raw band powers (δ θ α β γ) as live bars, each normalised to the loudest
    /// band so the display clearly shows brain-wave activity (and is flat when there's no signal).
    void RedrawBars(Color32[] px)
    {
        float max = 1e-6f;
        for (int i = 0; i < 5; i++) max = Mathf.Max(max, _bandsDisp[i]);

        const int n = 5;
        int gap   = 6;
        int barW  = (GraphW - gap * (n + 1)) / n;
        for (int bi = 0; bi < n; bi++)
        {
            float norm = Mathf.Clamp01(_bandsDisp[bi] / max);
            int   h    = Mathf.RoundToInt(norm * (GraphH - 2));
            int   x0   = gap + bi * (barW + gap);
            for (int x = x0; x < x0 + barW && x < GraphW; x++)
                for (int y = 0; y < h; y++)
                    px[y * GraphW + x] = CBand;
        }
    }

    void PlotLine(Color32[] px, float[] buf, Color32 col)
    {
        Color32 dim = new Color32(
            (byte)(col.r >> 1), (byte)(col.g >> 1), (byte)(col.b >> 1), col.a);

        for (int x = 0; x < GraphW; x++)
        {
            int idx = (_head + x) % GraphW;
            int y   = Mathf.Clamp(Mathf.RoundToInt(buf[idx] * (GraphH - 1)), 0, GraphH - 1);
            px[y * GraphW + x] = col;
            if (y + 1 < GraphH) px[(y + 1) * GraphW + x] = dim;
            if (y - 1 >= 0)     px[(y - 1) * GraphW + x] = dim;
        }
    }

    void ClearTex()
    {
        var px = new Color32[GraphW * GraphH];
        for (int i = 0; i < px.Length; i++) px[i] = CBack;
        _tex.SetPixels32(px);
        _tex.Apply();
    }

    // ── Canvas builder ────────────────────────────────────────────────────────
    void BuildHUD()
    {
        Camera cam = Camera.main;
        if (cam == null && Camera.allCamerasCount > 0)
        {
            cam = Camera.allCameras[0];
        }

        if (cam == null)
        {
            _buildRetries++;
            if (_buildRetries >= MaxBuildRetries)
            {
                Debug.LogError("[MuseStatusHUD] Camera not found after " +
                               $"{MaxBuildRetries} retries — HUD disabled. " +
                               "Make sure there is an active Camera in the scene.");
                enabled = false;
                return;
            }
            Invoke(nameof(BuildHUD), 0.5f);
            return;
        }

        // ── Root: World Space canvas, child of camera ──────────────────────
        var root = new GameObject("MuseHUD");
        root.transform.SetParent(cam.transform, worldPositionStays: false);
        root.transform.localPosition = hudPosition;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale    = Vector3.one * hudScale;

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1f;
        // Note: GraphicRaycaster is NOT added — it's a no-op in VR without OVR/XR raycaster integration.

        var rootRT = root.GetComponent<RectTransform>();
        rootRT.sizeDelta = new Vector2(PanelW, PanelH);

        // ── Panel (dark glass background) ─────────────────────────────────
        var panel = MakeRect(root.transform, "Panel");
        Stretch(panel);
        panel.gameObject.AddComponent<Image>().color = new Color(0.06f, 0.07f, 0.12f, 0.93f);

        // ── Title bar ─────────────────────────────────────────────────────
        var titleBar = MakeRect(panel, "TitleBar");
        Pin(titleBar, 0, PanelH - 26, PanelW, 26);
        var titleImg = titleBar.gameObject.AddComponent<Image>();
        titleImg.color = new Color(0.10f, 0.12f, 0.20f, 1f);

        var titleTxt = AddTMP(titleBar, "TitleText");
        Stretch(titleTxt.GetComponent<RectTransform>());
        titleTxt.text      = "  🧠  MUSE DEBUG HUD";
        titleTxt.fontSize  = 10.5f;
        titleTxt.fontStyle = FontStyles.Bold;
        titleTxt.color     = new Color(0.75f, 0.85f, 1.00f, 1f);
        titleTxt.alignment = TextAlignmentOptions.Left;
        titleTxt.textWrappingMode = TextWrappingModes.NoWrap;

        // ── Status label ──────────────────────────────────────────────────
        var statusRT = MakeRect(panel, "Status");
        Pin(statusRT, 8, PanelH - 52, PanelW - 16, 22);
        _statusLabel = AddTMP(statusRT, "StatusText");
        Stretch(_statusLabel.GetComponent<RectTransform>());
        _statusLabel.text     = $"<color={HexScan}>●</color>  Initializing...";
        _statusLabel.fontSize = 10f;
        _statusLabel.color    = Color.white;
        _statusLabel.textWrappingMode = TextWrappingModes.NoWrap;
        _statusLabel.overflowMode       = TextOverflowModes.Ellipsis;
        _statusLabel.alignment          = TextAlignmentOptions.Left;

        // ── Thin divider ──────────────────────────────────────────────────
        var divRT = MakeRect(panel, "Divider");
        Pin(divRT, 8, PanelH - 56, PanelW - 16, 1);
        var divImg = divRT.gameObject.AddComponent<Image>();
        divImg.color = new Color(1f, 1f, 1f, 0.12f);

        // ── Graph ─────────────────────────────────────────────────────────
        var graphRT = MakeRect(panel, "Graph");
        Pin(graphRT, 8, PanelH - 56 - GraphH - 4, GraphW, GraphH);
        _graphImg         = graphRT.gameObject.AddComponent<RawImage>();
        _graphImg.texture = _tex;

        // ── Numeric readout ───────────────────────────────────────────────
        var valRT = MakeRect(panel, "Values");
        Pin(valRT, 8, 30, PanelW - 16, 18);
        _valuesLabel = AddTMP(valRT, "ValuesText");
        Stretch(_valuesLabel.GetComponent<RectTransform>());
        _valuesLabel.text      = "waiting for data...";
        _valuesLabel.fontSize  = 8.5f;
        _valuesLabel.color     = new Color(0.85f, 0.85f, 0.85f, 1f);
        _valuesLabel.textWrappingMode = TextWrappingModes.NoWrap;
        _valuesLabel.alignment          = TextAlignmentOptions.Left;

        // ── Legend ────────────────────────────────────────────────────────
        var legRT = MakeRect(panel, "Legend");
        Pin(legRT, 8, 12, PanelW - 16, 16);
        _legendLabel = AddTMP(legRT, "LegendText");
        Stretch(_legendLabel.GetComponent<RectTransform>());
        _legendLabel.text =                   // replaced live by AddSample once data arrives
            $"<color={HexOK}>■</color> Stress    <color=#FF9426>■</color> Cognitive load    " +
            "<color=#489EFF>■</color> Attention    grid = 30 s  (0–1)";
        _legendLabel.fontSize  = 8f;
        _legendLabel.color     = new Color(0.65f, 0.68f, 0.75f, 1f);
        _legendLabel.alignment = TextAlignmentOptions.Center;
        _legendLabel.textWrappingMode = TextWrappingModes.NoWrap;
    }

    // ── Layout helpers ────────────────────────────────────────────────────────

    /// Create an empty RectTransform child.
    static RectTransform MakeRect(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, worldPositionStays: false);
        return go.AddComponent<RectTransform>();
    }

    /// Pin to bottom-left corner with absolute pixel coords.
    static void Pin(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin        = Vector2.zero;
        rt.anchorMax        = Vector2.zero;
        rt.pivot            = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta        = new Vector2(w, h);
    }

    /// Stretch to fill parent.
    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    /// Add a TextMeshProUGUI component on a child of parent (RectTransform).
    /// Note: TextMeshProUGUI already requires a RectTransform — do NOT add one manually first.
    static TextMeshProUGUI AddTMP(RectTransform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, worldPositionStays: false);
        // TextMeshProUGUI.Awake adds RectTransform automatically — don't add it beforehand.
        return go.AddComponent<TextMeshProUGUI>();
    }

    private void OnDestroy()
    {
        if (_tex != null) Destroy(_tex);
    }
}
