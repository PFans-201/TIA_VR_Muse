using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// Transient adaptive-event readout: short text that appears near the BOTTOM of the player's
/// view whenever the game eases conditions, then fades away — it is NOT a fixed, always-on
/// panel. Each line is tagged with the signal that drove the action:
///   • [MUSE] blue       — driven by the Muse S stress reading
///   • [BEHAVIOUR] amber — driven by in-game behaviour (time on a piece, grabs, lost in the dark)
///
/// Self-contained: drop on any GameObject; it builds its own World Space canvas as a child
/// of Camera.main at runtime and subscribes to AdaptiveEventBus. See [[AdaptiveEventBus]].
public class AdaptiveEventHUD : MonoBehaviour
{
    [Header("VR Position (camera-local metres)")]
    [Tooltip("Where to place the toast relative to the headset camera. Default = low and centred.")]
    public Vector3 hudPosition = new Vector3(0f, -0.30f, 0.75f);
    [Tooltip("World-scale per canvas pixel.")]
    public float hudScale = 0.0011f;

    [Header("Behaviour")]
    [Tooltip("How long (seconds) each event stays before it fully fades out.")]
    public float holdSeconds = 14f;
    [Tooltip("Maximum number of events shown at once (newest at the bottom).")]
    public int maxLines = 3;

    private const int PanelW = 560;
    private const int PanelH = 220;   // taller so the larger 3-line text isn't clipped
    private const string HexMuse = "#5AA8FF";   // Muse stress
    private const string HexGame = "#FFA838";   // behaviour

    private readonly List<AdaptiveEventBus.AdaptiveEvent> _events = new();
    private CanvasGroup     _group;
    private TextMeshProUGUI _body;
    private int   _buildRetries;
    private const int MaxBuildRetries = 10;

    private void OnEnable()  => AdaptiveEventBus.OnEvent += HandleEvent;
    private void OnDisable() => AdaptiveEventBus.OnEvent -= HandleEvent;

    private void Start() => BuildHUD();

    private void HandleEvent(AdaptiveEventBus.AdaptiveEvent e)
    {
        _events.Add(e);
        if (_events.Count > 32) _events.RemoveRange(0, _events.Count - 32);
    }

    private void Update()
    {
        if (_group == null || _body == null) return;

        float now = Time.time;
        _events.RemoveAll(e => now - e.time > holdSeconds);

        if (_events.Count == 0)
        {
            _group.alpha = 0f;          // fully invisible when idle — nothing fixed on screen
            return;
        }

        // Show the most recent events, oldest at top so the newest reads at the bottom.
        var sb = new System.Text.StringBuilder();
        int start = Mathf.Max(0, _events.Count - maxLines);
        float newestAlpha = 0f;
        for (int i = start; i < _events.Count; i++)
        {
            var   e   = _events[i];
            float a   = Mathf.Clamp01(1f - (now - e.time) / holdSeconds);   // 1 → 0 over hold
            newestAlpha = Mathf.Max(newestAlpha, a);
            int    al  = Mathf.RoundToInt(Mathf.Lerp(40f, 255f, a));
            string hex = e.signal == AdaptiveSignal.MuseStress ? HexMuse : HexGame;
            string tag = e.signal == AdaptiveSignal.MuseStress ? "MUSE" : "BEHAVIOUR";
            sb.Append($"<alpha=#{al:X2}><color={hex}><b>[{tag}]</b></color> {e.action}");
            if (i < _events.Count - 1) sb.Append('\n');
        }

        _body.text   = sb.ToString();
        _group.alpha = newestAlpha;     // the whole toast fades out with its freshest event
    }

    // ── Canvas builder (head-locked, no persistent chrome) ─────────────────────
    private void BuildHUD()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            if (++_buildRetries >= MaxBuildRetries)
            {
                Debug.LogError("[AdaptiveEventHUD] Camera.main not found — HUD disabled. Tag your XR camera 'MainCamera'.");
                enabled = false;
                return;
            }
            Invoke(nameof(BuildHUD), 0.5f);
            return;
        }

        var root = new GameObject("AdaptiveEventHUD_Canvas");
        root.transform.SetParent(cam.transform, worldPositionStays: false);
        root.transform.localPosition = hudPosition;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale    = Vector3.one * hudScale;

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1f;
        root.GetComponent<RectTransform>().sizeDelta = new Vector2(PanelW, PanelH);
        _group = root.AddComponent<CanvasGroup>();
        _group.alpha = 0f;

        // Faint backing strip purely for legibility — it fades out with the text (via the
        // CanvasGroup), so nothing stays on screen when there are no events.
        var bg = MakeRect(root.transform, "Backing");
        Stretch(bg);
        bg.gameObject.AddComponent<Image>().color = new Color(0.05f, 0.06f, 0.10f, 0.55f);

        var bodyRT = MakeRect(root.transform, "Body");
        Stretch(bodyRT);
        _body = AddTMP(bodyRT, "BodyText");
        Stretch(_body.GetComponent<RectTransform>());
        _body.text      = "";
        _body.fontSize  = 32f;                            // larger so the hint reads even when blurry
        _body.color     = Color.white;
        _body.alignment = TextAlignmentOptions.Center;   // centred in the box (both axes)
        _body.textWrappingMode = TextWrappingModes.Normal;
        _body.richText  = true;
        _body.margin    = new Vector4(10f, 6f, 10f, 6f);
    }

    // ── Layout helpers ─────────────────────────────────────────────────────────
    private static RectTransform MakeRect(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, worldPositionStays: false);
        return go.AddComponent<RectTransform>();
    }
    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }
    private static TextMeshProUGUI AddTMP(RectTransform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, worldPositionStays: false);
        return go.AddComponent<TextMeshProUGUI>();
    }
}
