using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// Scene 2 (Tutorial) of the redesigned session flow.
///
/// Shows panels (and optionally a control-demo video) explaining locomotion and how to
/// grab/interact with objects. Meanwhile the Muse runs the TUTORIAL baseline phase:
/// it streams stress vs the REST reference (tracking the peak, which drives the
/// difficulty recommendation) while collecting the active-VR reference used in-game.
///
/// After a minimum time, a "you can continue, or stay longer" popup appears with a
/// Continue button. On Continue it commits the active reference and loads the game scene.
public class TutorialController : MonoBehaviour
{
    [Header("Flow")]
    [Tooltip("Scene loaded when the user presses Continue.")]
    public string nextScene = "03_Game";
    [Tooltip("Minimum tutorial time before the Continue popup appears (can extend freely).")]
    public float minSeconds = 40f;

    [Header("UI (optional — wired by the scene builder)")]
    public GameObject infoPanels;     // control explanations (+ optional video)
    public GameObject proceedPanel;   // "continue or stay" popup, hidden until minSeconds
    public Button     continueButton;
    public TMP_Text   timerLabel;

    [Header("Bridge handshake")]
    [Tooltip("Seconds to wait for the bridge to acknowledge a baseline command before " +
             "proceeding anyway (UDP path only; the on-device path is synchronous).")]
    public float ackTimeout = 2f;

    // On-device direct adapter (Quest BLE) takes precedence, else the UDP bridge adapter.
    private IMuseBaselineControl _baseline;
    private bool _canProceed;

    private void Start()
    {
        if (MuseDirectAdapter.Instance != null) _baseline = MuseDirectAdapter.Instance;
        else                                    _baseline = MuseUdpAdapter.Instance;

        if (proceedPanel != null)   proceedPanel.SetActive(false);
        if (continueButton != null) continueButton.onClick.AddListener(Proceed);

        StartCoroutine(BeginTutorialBaseline());   // stream vs rest + collect active reference
        StartCoroutine(MinTimer());
    }

    private IEnumerator BeginTutorialBaseline()
    {
        // On-device path streams stress-vs-rest while collecting the active reference; the UDP
        // bridge has a single active-VR baseline phase. Wait for the bridge's ack either way.
        if (_baseline is MuseDirectAdapter direct)
        {
            direct.StartTutorialBaseline();
        }
        else
        {
            _baseline?.StartActiveBaseline();
            yield return WaitForAck(MuseUdpAdapter.CmdActiveStart);
        }
    }

    private IEnumerator MinTimer()
    {
        float t = minSeconds;
        while (t > 0f)
        {
            if (timerLabel != null)
                timerLabel.text = $"Get used to the controls...  {Mathf.CeilToInt(t)}s";
            t -= Time.deltaTime;
            yield return null;
        }
        _canProceed = true;
        if (timerLabel != null)   timerLabel.text = "Ready when you are.";
        if (proceedPanel != null) proceedPanel.SetActive(true);
    }

    private void Proceed()
    {
        if (!_canProceed) return;   // ignore early clicks before the minimum elapses
        if (continueButton != null) continueButton.interactable = false;
        StartCoroutine(FinalizeAndLoad());
    }

    private IEnumerator FinalizeAndLoad()
    {
        // Commit the active-VR reference, then wait for the bridge to confirm it finalized and
        // switched to streaming before loading the game — so the puzzle reads real stress, not
        // the neutral 0.5 the bridge emits during the baseline phase.
        _baseline?.FinalizeBaseline();
        yield return WaitForAck(MuseUdpAdapter.CmdActiveStop);
        SceneManager.LoadScene(nextScene);
    }

    /// On the UDP path, blocks until the bridge acknowledges <paramref name="cmd"/> (or the
    /// timeout elapses, then proceeds with a warning). No-op on the on-device/no-EEG paths.
    private IEnumerator WaitForAck(string cmd)
    {
        if (_baseline is not MuseUdpAdapter udp) yield break;
        float t = 0f;
        while (t < ackTimeout)
        {
            if (udp.ConsumeAck(cmd)) { Debug.Log($"[TutorialController] bridge acked '{cmd}'."); yield break; }
            t += Time.deltaTime;
            yield return null;
        }
        Debug.LogWarning($"[TutorialController] no '{cmd}' ack within {ackTimeout}s — proceeding anyway.");
    }
}
