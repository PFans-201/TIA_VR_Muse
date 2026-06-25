using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;

/// Self-contained Muse debug overlay for VR (Quest).
///
/// Drop on any GameObject in the scene — at runtime it builds its own World Space window parented
/// to the camera, so it stays FIXED in the player's view (head-locked) and never drifts around the
/// scene.
///
/// Window controls:
///   • The "–" button in the title bar collapses the window to a small circle.
///   • Click the circle to open the window again.
///
/// Phase-aware content:
///   • During the BASELINES  bars of just the brain-wave bands we actually use to compute the
///                           indices — θ (theta), α (alpha), β (beta) — each a distinct colour,
///                           plus signal quality. (No standardized indices exist yet.)
///   • During GAMEPLAY       the three standardized 0–1 indices as connected LINE plots, like the
///                           PC plot: stress, cognitive load, attention. Raw z-scores are hidden.
public class MuseStatusHUD : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Fixed placement (camera-local metres)")]
    [Tooltip("Where the head-locked window sits relative to the headset (X+ right, Y- down, Z+ forward).")]
    public Vector3 spawnOffset = new Vector3(0.30f, -0.20f, 0.65f);

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
    private const int CircleD  = 90;   // minimized-circle diameter (px)

    // ── Ring buffers for the gameplay graph (the three standardized 0–1 indices) ──
    private readonly float[] _sBuf = new float[GraphW];  // stress
    private readonly float[] _cBuf = new float[GraphW];  // cognitive load
    private readonly float[] _aBuf = new float[GraphW];  // attention
    private int   _head;
    private float _sampleTimer;
    private bool  _hasData;
    private int   _buildRetries;
    private const int MaxBuildRetries = 10;  // 10 × 0.5s = 5s max wait for Camera.main

    // Baseline (calibration) state — only the bands used by the indices: θ, α, β.
    private bool _baselineMode;
    private readonly float[] _bandsDisp = new float[5];   // δ θ α β γ raw band powers (full packet)
    // _bandsDisp indices that feed the metrics: 1 θ, 2 α, 3 β (0 δ and 4 γ are unused → not shown).
    private static readonly int[]     k_UsedBandIdx  = { 1, 2, 3 };
    private static readonly string[]  k_UsedBandName = { "θ", "α", "β" };
    private static readonly Color32[] k_UsedBandCol  =
    {
        new Color32(150, 120, 255, 255),   // θ theta — violet
        new Color32( 52, 205, 140, 255),   // α alpha — green
        new Color32(255, 150,  60, 255),   // β beta  — orange
    };

    // ── Window state ────────────────────────────────────────────────────────────
    private bool       _expanded = true;
    private GameObject _expandedGO;
    private GameObject _minimizedGO;
    private GameObject _infoGO;       // opaque "how each metric is computed" overlay

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

    const string HexScan  = "#FF8020";
    const string HexConn  = "#FFD91A";
    const string HexOK    = "#33E04D";
    const string HexErr   = "#FF3344";

    // ─────────────────────────────────────────────────────────────────────────
    private void Start()
    {
        _tex = new Texture2D(GraphW, GraphH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        ClearTex();
        BuildHUD();
    }

    private void Update()
    {
        if (_expanded) RefreshStatus();

        _sampleTimer += Time.deltaTime;
        if (_sampleTimer >= sampleInterval)
        {
            _sampleTimer = 0f;
            AddSample();
            if (_expanded) RedrawGraph();
        }
    }

    // ── Status label ──────────────────────────────────────────────────────────
    void RefreshStatus()
    {
        if (_statusLabel == null) return;

        var mda = MuseDirectAdapter.Instance;
        var udp = MuseUdpAdapter.Instance;
        if (mda == null && udp == null)
        {
            _statusLabel.text = $"<color={HexErr}>●</color>  No Muse adapter in scene";
            return;
        }

        string status;
        float stressLevel = 0.5f;
        bool contact = false;
        bool useUdp = udp != null && (udp.Receiving || mda == null);

        if (useUdp)
        {
            status = udp.Status; stressLevel = udp.StressLevel; contact = udp.Receiving;
        }
        else if (mda != null)
        {
            status = mda.Status; stressLevel = mda.StressLevel; contact = mda.Contact;
        }
        else { status = "No adapter"; }

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

        // ── Baseline calibration: no standardized indices yet → show live raw bands (θ α β) ──
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
            {
                string c0 = ColorHex(k_UsedBandCol[0]), c1 = ColorHex(k_UsedBandCol[1]), c2 = ColorHex(k_UsedBandCol[2]);
                _legendLabel.text =
                    $"<color={c0}>■</color> θ theta    <color={c1}>■</color> α alpha    " +
                    $"<color={c2}>■</color> β beta    (bands used for the metrics)";
            }
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

            // Connected LINE plots (back to front so stress reads on top), like the PC plot.
            PlotLine(px, _aBuf, CBlue);     // attention
            PlotLine(px, _cBuf, COrange);   // cognitive load
            PlotLine(px, _sBuf, CGreen);    // stress
        }

        _tex.SetPixels32(px);
        _tex.Apply();
    }

    // ── Baseline band bars (θ α β only, each its own colour) ─────────────────────
    void RedrawBars(Color32[] px)
    {
        float max = 1e-6f;
        foreach (int bi in k_UsedBandIdx) max = Mathf.Max(max, _bandsDisp[bi]);

        int n    = k_UsedBandIdx.Length;
        int gap  = 14;
        int barW = (GraphW - gap * (n + 1)) / n;
        for (int k = 0; k < n; k++)
        {
            float norm = Mathf.Clamp01(_bandsDisp[k_UsedBandIdx[k]] / max);
            int   h    = Mathf.RoundToInt(norm * (GraphH - 2));
            int   x0   = gap + k * (barW + gap);
            Color32 col = k_UsedBandCol[k];
            for (int x = x0; x < x0 + barW && x < GraphW; x++)
                for (int y = 0; y < h; y++)
                    px[y * GraphW + x] = col;
        }
    }

    /// Draws a CONNECTED line for a ring buffer: each column fills the pixels between the previous
    /// sample's height and this one's, so consecutive samples join into a continuous line (not dots).
    void PlotLine(Color32[] px, float[] buf, Color32 col)
    {
        int prevY = -1;
        for (int x = 0; x < GraphW; x++)
        {
            int idx = (_head + x) % GraphW;
            int y   = Mathf.Clamp(Mathf.RoundToInt(buf[idx] * (GraphH - 1)), 0, GraphH - 1);
            if (prevY < 0) prevY = y;
            int lo = Mathf.Min(prevY, y), hi = Mathf.Max(prevY, y);
            for (int yy = lo; yy <= hi; yy++) px[yy * GraphW + x] = col;
            prevY = y;
        }
    }

    void ClearTex()
    {
        var px = new Color32[GraphW * GraphH];
        for (int i = 0; i < px.Length; i++) px[i] = CBack;
        _tex.SetPixels32(px);
        _tex.Apply();
    }

    // ── Canvas builder (free-standing, draggable window) ────────────────────────
    void BuildHUD()
    {
        Camera cam = Camera.main;
        if (cam == null && Camera.allCamerasCount > 0) cam = Camera.allCameras[0];

        if (cam == null)
        {
            if (++_buildRetries >= MaxBuildRetries)
            {
                Debug.LogError("[MuseStatusHUD] Camera not found — HUD disabled. Ensure an active Camera exists.");
                enabled = false;
                return;
            }
            Invoke(nameof(BuildHUD), 0.5f);
            return;
        }

        // ── Root: World Space canvas PARENTED to the camera so it stays fixed in the player's
        //    view (head-locked) — it does not move around the scene. Raycasters let the controller
        //    ray click the minimize / info buttons.
        var root = new GameObject("MuseHUD");
        root.transform.SetParent(cam.transform, worldPositionStays: false);
        root.transform.localPosition = spawnOffset;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale    = Vector3.one * hudScale;

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1f;
        root.AddComponent<GraphicRaycaster>();
        root.AddComponent<TrackedDeviceGraphicRaycaster>();   // controller-ray clicks on the buttons
        root.GetComponent<RectTransform>().sizeDelta = new Vector2(PanelW, PanelH);

        EnsureEventSystem();

        BuildExpanded(root.transform);
        BuildMinimized(root.transform);
        SetExpanded(true);
    }

    void BuildExpanded(Transform rootT)
    {
        _expandedGO = MakeRect(rootT, "Expanded").gameObject;
        Stretch((RectTransform)_expandedGO.transform);

        var panel = MakeRect(_expandedGO.transform, "Panel");
        Stretch(panel);
        panel.gameObject.AddComponent<Image>().color = new Color(0.06f, 0.07f, 0.12f, 0.93f);

        // Title bar (holds the title + minimize / info buttons).
        var titleBar = MakeRect(panel, "TitleBar");
        Pin(titleBar, 0, PanelH - 26, PanelW, 26);
        titleBar.gameObject.AddComponent<Image>().color = new Color(0.10f, 0.12f, 0.20f, 1f);

        var titleTxt = AddTMP(titleBar, "TitleText");
        Stretch(titleTxt.GetComponent<RectTransform>());
        titleTxt.text      = "  🧠  MUSE";
        titleTxt.fontSize  = 10.5f;
        titleTxt.fontStyle = FontStyles.Bold;
        titleTxt.color     = new Color(0.75f, 0.85f, 1.00f, 1f);
        titleTxt.alignment = TextAlignmentOptions.Left;
        titleTxt.textWrappingMode = TextWrappingModes.NoWrap;

        // Minimize "–" button (top-right of the title bar) → collapse to circle.
        var minBtn = MakeButton(titleBar, "MinimizeBtn", "–", new Color(0.30f, 0.34f, 0.45f));
        Pin(minBtn.GetComponent<RectTransform>(), PanelW - 26, 2, 22, 22);
        minBtn.onClick.AddListener(() => SetExpanded(false));

        // Info "i" button (just left of minimize) → opens the metric-explanation overlay.
        var infoBtn = MakeButton(titleBar, "InfoBtn", "i", new Color(0.22f, 0.40f, 0.55f));
        Pin(infoBtn.GetComponent<RectTransform>(), PanelW - 50, 2, 22, 22);
        infoBtn.onClick.AddListener(() => ShowInfo(true));

        // Status label
        var statusRT = MakeRect(panel, "Status");
        Pin(statusRT, 8, PanelH - 52, PanelW - 16, 22);
        _statusLabel = AddTMP(statusRT, "StatusText");
        Stretch(_statusLabel.GetComponent<RectTransform>());
        _statusLabel.text     = $"<color={HexScan}>●</color>  Initializing...";
        _statusLabel.fontSize = 10f;
        _statusLabel.color    = Color.white;
        _statusLabel.textWrappingMode = TextWrappingModes.NoWrap;
        _statusLabel.overflowMode     = TextOverflowModes.Ellipsis;
        _statusLabel.alignment        = TextAlignmentOptions.Left;

        var divRT = MakeRect(panel, "Divider");
        Pin(divRT, 8, PanelH - 56, PanelW - 16, 1);
        divRT.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);

        var graphRT = MakeRect(panel, "Graph");
        Pin(graphRT, 8, PanelH - 56 - GraphH - 4, GraphW, GraphH);
        _graphImg         = graphRT.gameObject.AddComponent<RawImage>();
        _graphImg.texture = _tex;

        var valRT = MakeRect(panel, "Values");
        Pin(valRT, 8, 30, PanelW - 16, 18);
        _valuesLabel = AddTMP(valRT, "ValuesText");
        Stretch(_valuesLabel.GetComponent<RectTransform>());
        _valuesLabel.text      = "waiting for data...";
        _valuesLabel.fontSize  = 8.5f;
        _valuesLabel.color     = new Color(0.85f, 0.85f, 0.85f, 1f);
        _valuesLabel.textWrappingMode = TextWrappingModes.NoWrap;
        _valuesLabel.alignment        = TextAlignmentOptions.Left;

        var legRT = MakeRect(panel, "Legend");
        Pin(legRT, 8, 12, PanelW - 16, 16);
        _legendLabel = AddTMP(legRT, "LegendText");
        Stretch(_legendLabel.GetComponent<RectTransform>());
        _legendLabel.text =
            $"<color={HexOK}>■</color> Stress    <color=#FF9426>■</color> Cognitive load    " +
            "<color=#489EFF>■</color> Attention    grid = 30 s  (0–1)";
        _legendLabel.fontSize  = 8f;
        _legendLabel.color     = new Color(0.65f, 0.68f, 0.75f, 1f);
        _legendLabel.alignment = TextAlignmentOptions.Center;
        _legendLabel.textWrappingMode = TextWrappingModes.NoWrap;

        BuildInfoPanel(panel);   // opaque metric-explanation overlay (added last → drawn on top)
    }

    /// Opaque overlay that explains how each metric is computed. It fills the whole panel and is
    /// fully opaque, so no HUD content shows through behind it. Toggled by the title-bar "i" button.
    void BuildInfoPanel(Transform panelT)
    {
        _infoGO  = MakeRect(panelT, "InfoPanel").gameObject;
        var rt   = (RectTransform)_infoGO.transform;
        Stretch(rt);
        _infoGO.AddComponent<Image>().color = new Color(0.04f, 0.05f, 0.09f, 1f);   // alpha = 1 → opaque

        var title = AddTMP(rt, "InfoTitle");
        Pin(title.GetComponent<RectTransform>(), 12, PanelH - 30, PanelW - 24, 22);
        title.text      = "How each metric is computed";
        title.fontSize  = 12f;
        title.fontStyle = FontStyles.Bold;
        title.color     = new Color(0.80f, 0.88f, 1f, 1f);
        title.alignment = TextAlignmentOptions.Left;

        var body = AddTMP(rt, "InfoBody");
        Pin(body.GetComponent<RectTransform>(), 14, 30, PanelW - 28, PanelH - 64);
        body.fontSize  = 8.6f;
        body.color     = new Color(0.86f, 0.88f, 0.92f, 1f);
        body.alignment = TextAlignmentOptions.TopLeft;
        body.textWrappingMode = TextWrappingModes.Normal;
        body.text =
            "All three are measured against your active-VR baseline and squashed to 0–1 (sigmoid). " +
            "<b>0.50 = same as baseline</b>; higher = more.  (z = std-devs above baseline.)\n\n" +
            "<color=#33E04D><b>Stress</b></color> = sigmoid((βz − αz)/2) — rises as beta/arousal goes up and alpha drops.\n\n" +
            "<color=#FF9426><b>Cognitive load</b></color> = sigmoid((θz − αz)/2) — rises as frontal theta goes up and alpha drops.\n\n" +
            "<color=#489EFF><b>Attention</b></color> = sigmoid((βz − θz)/2) — the inverse Theta/Beta Ratio. " +
            "TBR = theta/beta; a LOW TBR (theta down, beta up) means focus, so attention rises as the TBR falls.\n\n" +
            "<i>Bands used: θ theta, α alpha, β beta (delta & gamma are not used).</i>";

        var close = MakeButton(rt, "InfoClose", "Close", new Color(0.40f, 0.44f, 0.52f));
        Pin(close.GetComponent<RectTransform>(), PanelW - 84, 6, 74, 20);
        close.onClick.AddListener(() => ShowInfo(false));

        _infoGO.SetActive(false);
    }

    void ShowInfo(bool on)
    {
        if (_infoGO != null) _infoGO.SetActive(on);
    }

    void BuildMinimized(Transform rootT)
    {
        _minimizedGO = MakeRect(rootT, "Minimized").gameObject;
        var rt = (RectTransform)_minimizedGO.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(CircleD, CircleD);

        // The circle is a button (click → expand). Fixed in view like the rest of the HUD.
        var img = _minimizedGO.AddComponent<Image>();
        img.color  = new Color(0.12f, 0.16f, 0.26f, 0.96f);
        img.sprite = CircleSprite();   // procedural so it stays round in a build
        img.type   = Image.Type.Simple;

        var btn = _minimizedGO.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(() => SetExpanded(true));

        var lbl = AddTMP(rt, "Icon");
        Stretch(lbl.GetComponent<RectTransform>());
        lbl.text      = "🧠";
        lbl.fontSize  = 34f;
        lbl.alignment = TextAlignmentOptions.Center;
        lbl.color     = new Color(0.80f, 0.88f, 1f, 1f);
    }

    void SetExpanded(bool on)
    {
        _expanded = on;
        if (_infoGO != null) _infoGO.SetActive(false);   // always start from the data view
        if (_expandedGO  != null) _expandedGO.SetActive(on);
        if (_minimizedGO != null) _minimizedGO.SetActive(!on);
        if (on) { RefreshStatus(); RedrawGraph(); }
    }

    // ── Layout helpers ────────────────────────────────────────────────────────

    static void EnsureEventSystem()
    {
        if (Object.FindFirstObjectByType<EventSystem>(FindObjectsInactive.Include) == null)
            new GameObject("EventSystem").AddComponent<EventSystem>();
    }

    private static Sprite _circleSprite;
    /// A procedurally-generated soft-edged white circle sprite (tinted by the Image colour), so the
    /// minimized toggle is round in a build without depending on Editor-only built-in sprites.
    static Sprite CircleSprite()
    {
        if (_circleSprite != null) return _circleSprite;
        const int d = 64;
        var tex = new Texture2D(d, d, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float r = d / 2f - 1f;
        var c   = new Vector2(d / 2f, d / 2f);
        var px  = new Color32[d * d];
        for (int y = 0; y < d; y++)
            for (int x = 0; x < d; x++)
            {
                float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c);
                float a    = Mathf.Clamp01(r - dist);   // ~1 px anti-aliased edge
                px[y * d + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        tex.SetPixels32(px);
        tex.Apply();
        _circleSprite = Sprite.Create(tex, new Rect(0, 0, d, d), new Vector2(0.5f, 0.5f));
        return _circleSprite;
    }

    static string ColorHex(Color32 c) => $"#{c.r:X2}{c.g:X2}{c.b:X2}";

    static RectTransform MakeRect(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, worldPositionStays: false);
        return go.AddComponent<RectTransform>();
    }

    static void Pin(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta        = new Vector2(w, h);
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    static TextMeshProUGUI AddTMP(RectTransform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, worldPositionStays: false);
        return go.AddComponent<TextMeshProUGUI>();   // adds its own RectTransform
    }

    static Button MakeButton(Transform parent, string name, string label, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, worldPositionStays: false);
        go.AddComponent<RectTransform>();
        var img = go.AddComponent<Image>();
        img.color = color;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var lbl = AddTMP(go.GetComponent<RectTransform>(), "Label");
        Stretch(lbl.GetComponent<RectTransform>());
        lbl.text = label; lbl.fontSize = 14f; lbl.fontStyle = FontStyles.Bold;
        lbl.alignment = TextAlignmentOptions.Center; lbl.color = Color.white;
        return btn;
    }

    private void OnDestroy()
    {
        if (_tex != null) Destroy(_tex);
    }
}
