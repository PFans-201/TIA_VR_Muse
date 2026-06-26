using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;

/// Small head-locked session menu with:
///   • Behaviour Help toggle — turns the behaviour-driven piece hints (PieceHintSystem) on/off.
///   • Muse Assist toggle — turns the MUSE-stress-driven assistance on/off (mechanical easing in
///                          AdaptiveDifficultyController + the stress speed-up of the hints). Both
///                          switches are independent and live in AssistanceSettings.
///   • Restart Puzzle — clears the board and reopens difficulty selection (PuzzleManager.AbortPuzzle).
///   • New Session    — resets the Muse calibration and reloads the intro scene, i.e. starts a
///                      fresh participant from the rest/active baseline again.
///
/// Self-building (like the Muse HUD): drop it on any GameObject and it makes its own world-space
/// canvas parented to the camera. It starts MINIMIZED as a small "≡" circle so it never clutters
/// the view; click it to open. "New User" asks for a second confirming tap (it restarts
/// everything), so it can't be triggered by accident.
public class SessionMenu : MonoBehaviour
{
    [Header("Fixed placement (camera-local metres)")]
    [Tooltip("Where the head-locked menu sits relative to the headset (default low-centre, at the " +
             "bottom of the view).")]
    public Vector3 anchor = new Vector3(0f, -0.33f, 0.62f);
    public float   scale  = 0.0008f;

    [Tooltip("Intro scene to reload for a new session (full Muse recalibration).")]
    public string introScene = "01_Intro";

    private const int PanelW = 300;
    private const int PanelH = 250;
    private const int CircleD = 80;

    private static readonly Color ToggleOnColor  = new Color(0.30f, 0.55f, 0.40f);
    private static readonly Color ToggleOffColor = new Color(0.34f, 0.34f, 0.40f);

    private GameObject _expandedGO;
    private GameObject _minimizedGO;
    private TextMeshProUGUI _newSessionLabel;
    private float _confirmUntil;          // New Session armed (awaiting 2nd tap) until this time
    private int   _buildRetries;

    private Image           _behaviorToggleImg;
    private TextMeshProUGUI _behaviorToggleLabel;
    private Image           _museToggleImg;
    private TextMeshProUGUI _museToggleLabel;

    private void Start()
    {
        AssistanceSettings.Reset();   // fresh participant → both helpers ON by default
        Build();
    }

    private void Update()
    {
        // Disarm the New-Session confirm if the player didn't tap again in time.
        if (_confirmUntil > 0f && Time.time > _confirmUntil)
        {
            _confirmUntil = 0f;
            if (_newSessionLabel != null) _newSessionLabel.text = "New User";
        }
    }

    // ── Actions ─────────────────────────────────────────────────────────────────
    private void RestartPuzzle()
    {
        var pm = FindFirstObjectByType<PuzzleManager>();
        if (pm != null) pm.AbortPuzzle();
        SetExpanded(false);
    }

    private void ToggleBehaviorHelper()
    {
        AssistanceSettings.SetBehaviorHelper(!AssistanceSettings.BehaviorHelperEnabled);
        RefreshToggles();
    }

    private void ToggleMuseHelper()
    {
        AssistanceSettings.SetMuseHelper(!AssistanceSettings.MuseHelperEnabled);
        RefreshToggles();
    }

    /// Paint each helper toggle to reflect its current on/off state.
    private void RefreshToggles()
    {
        if (_behaviorToggleImg != null)
            _behaviorToggleImg.color = AssistanceSettings.BehaviorHelperEnabled ? ToggleOnColor : ToggleOffColor;
        if (_behaviorToggleLabel != null)
            _behaviorToggleLabel.text = AssistanceSettings.BehaviorHelperEnabled ? "Behaviour Help:  ON" : "Behaviour Help:  OFF";

        if (_museToggleImg != null)
            _museToggleImg.color = AssistanceSettings.MuseHelperEnabled ? ToggleOnColor : ToggleOffColor;
        if (_museToggleLabel != null)
            _museToggleLabel.text = AssistanceSettings.MuseHelperEnabled ? "Muse Assist:  ON" : "Muse Assist:  OFF";
    }

    private void NewSession()
    {
        // First tap arms; second tap within 4 s actually restarts (avoids accidental full reset).
        if (_confirmUntil == 0f || Time.time > _confirmUntil)
        {
            _confirmUntil = Time.time + 4f;
            if (_newSessionLabel != null) _newSessionLabel.text = "Tap to confirm";
            return;
        }

        _confirmUntil = 0f;

        // Tell the PC bridge a new participant is starting: it saves the current user's session
        // (one PNG: baselines + gameplay metrics) and rolls over to a fresh per-user folder, so one
        // user's data never bleeds into the next. No-op on the BLE build (no UDP bridge).
        MuseUdpAdapter.Instance?.SendCommand("new_user");

        IMuseBaselineControl baseline = MuseDirectAdapter.Instance != null
            ? (IMuseBaselineControl)MuseDirectAdapter.Instance
            : MuseUdpAdapter.Instance;
        baseline?.ResetBaseline();                  // clear the committed baseline → recalibrate
        SceneManager.LoadScene(introScene);
    }

    private void SetExpanded(bool on)
    {
        if (_expandedGO  != null) _expandedGO.SetActive(on);
        if (_minimizedGO != null) _minimizedGO.SetActive(!on);
        if (!on) { _confirmUntil = 0f; if (_newSessionLabel != null) _newSessionLabel.text = "New User"; }
    }

    // ── Build ───────────────────────────────────────────────────────────────────
    private void Build()
    {
        Camera cam = Camera.main;
        if (cam == null && Camera.allCamerasCount > 0) cam = Camera.allCameras[0];
        if (cam == null)
        {
            if (++_buildRetries >= 10) { enabled = false; return; }
            Invoke(nameof(Build), 0.5f);
            return;
        }

        var root = new GameObject("SessionMenu");
        root.transform.SetParent(cam.transform, false);
        root.transform.localPosition = anchor;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale    = Vector3.one * scale;

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1f;
        root.AddComponent<GraphicRaycaster>();
        root.AddComponent<TrackedDeviceGraphicRaycaster>();
        root.GetComponent<RectTransform>().sizeDelta = new Vector2(PanelW, PanelH);
        if (FindFirstObjectByType<EventSystem>() == null)
            new GameObject("EventSystem").AddComponent<EventSystem>();

        // Expanded panel: title + two helper toggles + Restart/New-User buttons + minimize.
        _expandedGO = MakeRect(root.transform, "Expanded").gameObject;
        Stretch((RectTransform)_expandedGO.transform);
        _expandedGO.AddComponent<Image>().color = new Color(0.07f, 0.08f, 0.13f, 0.95f);

        var title = AddText(_expandedGO.transform, "Title", "Session",
                            new Vector2(0f, PanelH / 2f - 18f), new Vector2(PanelW - 16, 24), 16, FontStyles.Bold);
        title.alignment = TextAlignmentOptions.Center;

        var min = MakeButton(_expandedGO.transform, "Min", "–", new Vector2(PanelW / 2f - 18f, PanelH / 2f - 16f),
                             new Vector2(24, 24), new Color(0.30f, 0.34f, 0.45f));
        min.onClick.AddListener(() => SetExpanded(false));

        // Two independent helper switches — tap to flip ON/OFF (colour + label update).
        var behaviorToggle = MakeButton(_expandedGO.transform, "BehaviorToggle", "Behaviour Help:  ON",
                                        new Vector2(0f, 66f), new Vector2(PanelW - 40, 34), ToggleOnColor);
        behaviorToggle.onClick.AddListener(ToggleBehaviorHelper);
        _behaviorToggleImg   = behaviorToggle.targetGraphic as Image;
        _behaviorToggleLabel = behaviorToggle.GetComponentInChildren<TextMeshProUGUI>();

        var museToggle = MakeButton(_expandedGO.transform, "MuseToggle", "Muse Assist:  ON",
                                    new Vector2(0f, 28f), new Vector2(PanelW - 40, 34), ToggleOnColor);
        museToggle.onClick.AddListener(ToggleMuseHelper);
        _museToggleImg   = museToggle.targetGraphic as Image;
        _museToggleLabel = museToggle.GetComponentInChildren<TextMeshProUGUI>();

        var restart = MakeButton(_expandedGO.transform, "Restart", "Restart Puzzle",
                                 new Vector2(0f, -22f), new Vector2(PanelW - 40, 38), new Color(0.34f, 0.50f, 0.40f));
        restart.onClick.AddListener(RestartPuzzle);

        var newS = MakeButton(_expandedGO.transform, "NewSession", "New User",
                              new Vector2(0f, -66f), new Vector2(PanelW - 40, 38), new Color(0.52f, 0.40f, 0.36f));
        newS.onClick.AddListener(NewSession);
        _newSessionLabel = newS.GetComponentInChildren<TextMeshProUGUI>();

        RefreshToggles();

        // Minimized: a small circle with "≡".
        _minimizedGO = MakeRect(root.transform, "Minimized").gameObject;
        var mrt = (RectTransform)_minimizedGO.transform;
        mrt.anchorMin = mrt.anchorMax = new Vector2(0.5f, 0.5f);
        mrt.pivot = new Vector2(0.5f, 0.5f);
        mrt.anchoredPosition = Vector2.zero;
        mrt.sizeDelta = new Vector2(CircleD, CircleD);
        var cimg = _minimizedGO.AddComponent<Image>();
        cimg.color  = new Color(0.12f, 0.16f, 0.26f, 0.96f);
        cimg.sprite = CircleSprite();
        var cbtn = _minimizedGO.AddComponent<Button>();
        cbtn.targetGraphic = cimg;
        cbtn.onClick.AddListener(() => SetExpanded(true));
        var icon = AddText(mrt, "Icon", "≡", Vector2.zero, new Vector2(CircleD, CircleD), 34, FontStyles.Bold);
        icon.alignment = TextAlignmentOptions.Center;

        SetExpanded(false);   // start out of the way
    }

    // ── UI helpers ──────────────────────────────────────────────────────────────
    private static RectTransform MakeRect(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.AddComponent<RectTransform>();
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    private static TextMeshProUGUI AddText(Transform parent, string name, string text,
                                           Vector2 pos, Vector2 size, int fontSize, FontStyles style)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta        = size;
        var t = go.AddComponent<TextMeshProUGUI>();
        t.text = text; t.fontSize = fontSize; t.fontStyle = style;
        t.color = Color.white; t.alignment = TextAlignmentOptions.Center;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        return t;
    }

    private static Button MakeButton(Transform parent, string name, string label,
                                     Vector2 pos, Vector2 size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta        = size;
        var img = go.AddComponent<Image>();
        img.color = color;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var lbl = AddText(go.transform, "Label", label, Vector2.zero, size - new Vector2(10, 6), 15, FontStyles.Bold);
        Stretch(lbl.GetComponent<RectTransform>());
        return btn;
    }

    private static Sprite _circle;
    private static Sprite CircleSprite()
    {
        if (_circle != null) return _circle;
        const int d = 64;
        var tex = new Texture2D(d, d, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float r = d / 2f - 1f; var c = new Vector2(d / 2f, d / 2f);
        var px = new Color32[d * d];
        for (int y = 0; y < d; y++)
            for (int x = 0; x < d; x++)
            {
                float a = Mathf.Clamp01(r - Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c));
                px[y * d + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        tex.SetPixels32(px); tex.Apply();
        _circle = Sprite.Create(tex, new Rect(0, 0, d, d), new Vector2(0.5f, 0.5f));
        return _circle;
    }
}
