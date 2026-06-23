using UnityEngine;
using TMPro;

public class MuseSimpleConnectionHUD : MonoBehaviour
{
    private TextMeshProUGUI _text;
    private int _retries = 0;

    void Start()
    {
        Invoke(nameof(BuildSimpleHUD), 0.5f);
    }

    void BuildSimpleHUD()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            foreach (var c in FindObjectsOfType<Camera>())
            {
                if (c.isActiveAndEnabled && c.targetTexture == null)
                {
                    cam = c;
                    break;
                }
            }
        }

        if (cam == null)
        {
            _retries++;
            if (_retries > 10) return;
            Invoke(nameof(BuildSimpleHUD), 0.5f);
            return;
        }

        var root = new GameObject("MuseSimpleHUD");
        root.transform.SetParent(cam.transform, false);
        // Place it slightly higher and more to the left than the main HUD
        root.transform.localPosition = new Vector3(-0.25f, 0.15f, 0.65f);
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one * 0.001f;

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = root.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(800, 300);

        var bgGo = new GameObject("Bg");
        bgGo.transform.SetParent(root.transform, false);
        var bgImg = bgGo.AddComponent<UnityEngine.UI.Image>();
        bgImg.color = new Color(0.1f, 0.1f, 0.1f, 0.8f);
        var bgRt = bgGo.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.sizeDelta = Vector2.zero;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(root.transform, false);
        _text = textGo.AddComponent<TextMeshProUGUI>();
        _text.alignment = TextAlignmentOptions.Center;
        _text.fontSize = 32;
        _text.enableWordWrapping = false;
        _text.overflowMode = TextOverflowModes.Overflow;
        
        var errGo = new GameObject("ErrText");
        errGo.transform.SetParent(root.transform, false);
        var errText = errGo.AddComponent<TextMeshProUGUI>();
        errText.alignment = TextAlignmentOptions.TopLeft;
        errText.fontSize = 18;
        errText.color = Color.red;
        errText.overflowMode = TextOverflowModes.Overflow;
        var errRt = errGo.GetComponent<RectTransform>();
        errRt.anchorMin = Vector2.zero;
        errRt.anchorMax = Vector2.one;
        errRt.sizeDelta = Vector2.zero;
        
        Application.logMessageReceived += (condition, stackTrace, type) => {
            if (type == LogType.Exception || type == LogType.Error) {
                if (errText != null) errText.text += "\n" + condition;
            }
        };
        
        var txtRt = textGo.GetComponent<RectTransform>();
        txtRt.anchorMin = Vector2.zero;
        txtRt.anchorMax = Vector2.one;
        txtRt.sizeDelta = Vector2.zero;
    }

    private bool _hooked = false;

    void Update()
    {
        if (!_hooked)
        {
            var rawAd = FindObjectOfType<Android.BLE.BleAdapter>();
            if (rawAd != null)
            {
                rawAd.OnMessageReceived += (obj) => {
                    string msg = $"<color=yellow>BLE MSG: {obj.Command}</color>";
                    if (GameObject.Find("ErrText")?.GetComponent<TextMeshProUGUI>() is TextMeshProUGUI t) {
                        if (t.text.Length > 500) t.text = "";
                        t.text += "\n" + msg;
                    }
                };
                _hooked = true;
            }
        }
        
        if (_text == null) return;
        var adapter = MuseDirectAdapter.Instance;
        if (adapter == null)
        {
            _text.text = "<color=#FF5555>Muse Adapter Missing</color>";
            return;
        }

        if (adapter.Contact)
        {
            _text.text = "<color=#55FF55>MUSE CONNECTED</color>";
        }
         if (adapter.Status.ToLower().Contains("scanning"))
        {
            _text.text = $"<color=#FFAA00>SCANNING FOR MUSE...</color>\n<size=18>Seeing: {VelorexeBleTransport.LastDiscoveredDevice}</size>";
        }
        else
        {
            _text.text = $"<color=#FF5555>NOT CONNECTED\n({adapter.Status})</color>";
        }
    }
}
