using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// Self-contained Muse debug overlay for VR (Quest).
///
/// Drop on any GameObject in the scene (it creates its own World Space canvas
/// as a child of Camera.main at runtime, so no manual scene wiring needed).
///
/// Shows:
///   • Connection status dot  (orange=scanning, yellow=connecting, green=streaming, red=error)
///   • Scrolling graph        (green=stress 0-1, blue=theta-Z ±3σ, orange=alpha-Z ±3σ)
///   • Live numeric readout   (stress, thetaZ, alphaZ, CLI, active channels)
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

    // ── Ring buffers for the graph ────────────────────────────────────────────
    private readonly float[] _sBuf = new float[GraphW];  // stress
    private readonly float[] _tBuf = new float[GraphW];  // theta-Z normalised
    private readonly float[] _aBuf = new float[GraphW];  // alpha-Z normalised
    private int   _head;
    private float _sampleTimer;
    private bool  _hasData;
    private int   _buildRetries;
    private const int MaxBuildRetries = 10;  // 10 × 0.5s = 5s max wait for Camera.main

    // ── UI refs ───────────────────────────────────────────────────────────────
    private TextMeshProUGUI _statusLabel;
    private TextMeshProUGUI _valuesLabel;
    private RawImage        _graphImg;
    private Texture2D       _tex;

    // ── Palette ───────────────────────────────────────────────────────────────
    static readonly Color32 CBack   = new Color32( 10,  12,  20, 235);
    static readonly Color32 CGrid   = new Color32( 70,  75,  95,  45);
    static readonly Color32 CGreen  = new Color32( 48, 210,  88, 255);   // stress
    static readonly Color32 CBlue   = new Color32( 72, 158, 255, 255);   // theta
    static readonly Color32 COrange = new Color32(255, 148,  38, 255);   // alpha

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

        var mda = MuseDirectAdapter.Instance;
        if (mda == null)
        {
            _statusLabel.text = $"<color={HexErr}>●</color>  MuseDirectAdapter not in scene";
            return;
        }

        string status = mda.Status;
        string hex;
        if      (status.Contains("streaming"))                                       hex = HexOK;
        else if (status.Contains("connecting") || status.Contains("subscribing") ||
                 status.Contains("sending")    || status.Contains("GATT") ||
                 status.Contains("commands"))                                         hex = HexConn;
        else if (status.Contains("scanning"))                                        hex = HexScan;
        else if (status.Contains("no ") || status.Contains("error") ||
                 status.Contains("Error") || status.Contains("failed"))              hex = HexErr;
        else                                                                         hex = HexScan;

        string extra = status.Contains("streaming")
            ? $"   stress: <b>{mda.StressLevel:F3}</b>   " +
              $"contact: {(mda.Contact ? $"<color={HexOK}><b>✓</b></color>" : $"<color={HexErr}><b>✗</b></color>")}"
            : string.Empty;

        _statusLabel.text = $"<color={hex}>●</color>  {status}{extra}";
    }

    // ── Graph sample ──────────────────────────────────────────────────────────
    void AddSample()
    {
        var mda = MuseDirectAdapter.Instance;
        if (mda == null) return;

        var r = mda.LastReading;
        _sBuf[_head] = r.stress;
        _tBuf[_head] = Mathf.InverseLerp(-3f, 3f, r.thetaZ);  // ±3σ → 0–1
        _aBuf[_head] = Mathf.InverseLerp(-3f, 3f, r.alphaZ);
        _head = (_head + 1) % GraphW;
        _hasData = true;

        // Numeric readout
        if (_valuesLabel != null)
        {
            _valuesLabel.text =
                $"<color={HexOK}>stress {r.stress:F3}</color>    " +
                $"<color=#50A0FF>θz {r.thetaZ:+0.00;-0.00;+0.00}</color>    " +
                $"<color=#FF9628>αz {r.alphaZ:+0.00;-0.00;+0.00}</color>    " +
                $"CLI {r.cli:+0.00;-0.00;+0.00}    " +
                $"ch: <b>{(string.IsNullOrEmpty(r.usedChannels) ? "—" : r.usedChannels)}</b>";
        }
    }

    // ── Graph redraw ──────────────────────────────────────────────────────────
    void RedrawGraph()
    {
        if (!_hasData) return;

        var px = _tex.GetPixels32();

        // Background
        for (int i = 0; i < px.Length; i++) px[i] = CBack;

        // Horizontal grid lines at 0.25, 0.5, 0.75
        foreach (float f in new float[] { 0.25f, 0.5f, 0.75f })
        {
            int gy = Mathf.RoundToInt(f * (GraphH - 1));
            for (int x = 0; x < GraphW; x++) px[gy * GraphW + x] = CGrid;
        }

        // Vertical grid every 60 samples  (60 × 0.5 s = 30 s intervals)
        for (int x = 0; x < GraphW; x += 60)
            for (int y = 0; y < GraphH; y++) px[y * GraphW + x] = CGrid;

        // Plot lines (back to front so stress is on top)
        PlotLine(px, _tBuf, CBlue);
        PlotLine(px, _aBuf, COrange);
        PlotLine(px, _sBuf, CGreen);

        _tex.SetPixels32(px);
        _tex.Apply();
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
        if (cam == null)
        {
            // Find the active XR camera (ignore disabled cameras or reflection probes)
            foreach (var c in FindObjectsOfType<Camera>()) {
                if (c.isActiveAndEnabled && c.targetTexture == null) {
                    cam = c;
                    break;
                }
            }
        }
        
        if (cam == null)
        {
            _buildRetries++;
            if (_buildRetries >= MaxBuildRetries)
            {
                Debug.LogError("[MuseStatusHUD] No Camera found in scene after " +
                               $"{MaxBuildRetries} retries — HUD disabled.");
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
        titleTxt.enableWordWrapping = false;

        // ── Status label ──────────────────────────────────────────────────
        var statusRT = MakeRect(panel, "Status");
        Pin(statusRT, 8, PanelH - 52, PanelW - 16, 22);
        _statusLabel = AddTMP(statusRT, "StatusText");
        Stretch(_statusLabel.GetComponent<RectTransform>());
        _statusLabel.text     = $"<color={HexScan}>●</color>  Initializing...";
        _statusLabel.fontSize = 10f;
        _statusLabel.color    = Color.white;
        _statusLabel.enableWordWrapping = false;
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
        _valuesLabel.enableWordWrapping = false;
        _valuesLabel.alignment          = TextAlignmentOptions.Left;

        // ── Legend ────────────────────────────────────────────────────────
        var legRT = MakeRect(panel, "Legend");
        Pin(legRT, 8, 12, PanelW - 16, 16);
        var legTxt = AddTMP(legRT, "LegendText");
        Stretch(legTxt.GetComponent<RectTransform>());
        legTxt.text =
            $"<color={HexOK}>■</color> Stress (0–1)    " +
            "<color=#50A0FF>■</color> Theta-Z (±3σ)    " +
            "<color=#FF9628>■</color> Alpha-Z (±3σ)    " +
            "grid = 30 s";
        legTxt.fontSize  = 8f;
        legTxt.color     = new Color(0.65f, 0.68f, 0.75f, 1f);
        legTxt.alignment = TextAlignmentOptions.Center;
        legTxt.enableWordWrapping = false;
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
