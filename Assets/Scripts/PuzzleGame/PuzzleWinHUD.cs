using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// Celebratory "you win" readout. When the player completes a puzzle it pops a big, head-locked
/// banner — "✓ Solved!  Robot · Easy   Time 1:23" — that holds for a few seconds and fades out.
///
/// Self-building (like the Muse HUD): drop it on any GameObject; at runtime it makes its own
/// world-space canvas parented to the camera and subscribes to PuzzleManager.OnPuzzleCompleted.
/// The elapsed time comes from PuzzleManager.LastPuzzleSeconds.
public class PuzzleWinHUD : MonoBehaviour
{
    [Header("Placement (camera-local metres)")]
    public Vector3 anchor = new Vector3(0f, 0.12f, 0.95f);
    public float   scale  = 0.0011f;

    [Tooltip("Seconds the win banner stays fully visible before it fades out.")]
    public float holdSeconds = 8f;
    [Tooltip("Seconds the banner takes to fade out after the hold.")]
    public float fadeSeconds = 1.5f;

    private const int PanelW = 620;
    private const int PanelH = 200;

    private CanvasGroup     _group;
    private TextMeshProUGUI _title;
    private TextMeshProUGUI _body;
    private float           _shownAt = -999f;
    private bool            _active;
    private int             _buildRetries;
    private PuzzleManager   _pm;

    private void Start() => Build();

    private void OnEnable()
    {
        _pm = FindFirstObjectByType<PuzzleManager>();
        if (_pm != null) _pm.OnPuzzleCompleted += HandleCompleted;
    }

    private void OnDisable()
    {
        if (_pm != null) _pm.OnPuzzleCompleted -= HandleCompleted;
    }

    private void HandleCompleted(DifficultyLevel level)
    {
        if (_group == null) return;
        float secs = _pm != null ? _pm.LastPuzzleSeconds : 0f;
        int m = Mathf.FloorToInt(secs / 60f);
        int s = Mathf.FloorToInt(secs % 60f);
        if (_title != null) _title.text = "Puzzle solved!";
        if (_body  != null) _body.text  = $"Robot · {level}      Time  {m}:{s:00}";
        _shownAt = Time.time;
        _active  = true;
    }

    private void Update()
    {
        if (_group == null) return;
        if (!_active) { _group.alpha = 0f; return; }

        float t = Time.time - _shownAt;
        if (t <= holdSeconds) _group.alpha = 1f;
        else if (t <= holdSeconds + fadeSeconds)
            _group.alpha = 1f - (t - holdSeconds) / Mathf.Max(0.01f, fadeSeconds);
        else { _group.alpha = 0f; _active = false; }
    }

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

        var root = new GameObject("PuzzleWinHUD_Canvas");
        root.transform.SetParent(cam.transform, false);
        root.transform.localPosition = anchor;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale    = Vector3.one * scale;

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1f;
        root.GetComponent<RectTransform>().sizeDelta = new Vector2(PanelW, PanelH);
        _group = root.AddComponent<CanvasGroup>();
        _group.alpha = 0f;

        var bg = MakeRect(root.transform, "Backing");
        Stretch(bg);
        bg.gameObject.AddComponent<Image>().color = new Color(0.06f, 0.10f, 0.08f, 0.92f);

        _title = AddTMP(root.transform, "Title");
        Pin(_title.GetComponent<RectTransform>(), 0, PanelH / 2f - 8f, PanelW, PanelH / 2f - 8f);
        _title.text      = "";
        _title.fontSize  = 52f;
        _title.fontStyle = FontStyles.Bold;
        _title.color     = new Color(0.55f, 1f, 0.65f);
        _title.alignment = TextAlignmentOptions.Center;

        _body = AddTMP(root.transform, "Body");
        Pin(_body.GetComponent<RectTransform>(), 0, 12f, PanelW, PanelH / 2f - 12f);
        _body.text      = "";
        _body.fontSize  = 30f;
        _body.color     = Color.white;
        _body.alignment = TextAlignmentOptions.Center;
    }

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
    private static void Pin(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = new Vector2(0.5f, 0f); rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta        = new Vector2(w, h);
    }
    private static TextMeshProUGUI AddTMP(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.AddComponent<TextMeshProUGUI>();
    }
}
