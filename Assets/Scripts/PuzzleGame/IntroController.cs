using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// Scene 1 (Intro / blank room) of the redesigned session flow.
///
/// Shows an explanation panel ("relax, eyes open, for N seconds so we can measure your
/// resting baseline"). When the user presses Okay, the panel hides, the rest-baseline
/// count starts, and on completion it commits the REST reference and auto-advances to
/// the tutorial scene.
///
/// The Muse work is delegated to MuseDirectAdapter (the persistent on-device adapter).
/// If it isn't present (e.g. Editor/desktop without the direct path) the flow still runs
/// — you just don't get a real baseline. See [[redesign-session-flow]].
public class IntroController : MonoBehaviour
{
    [Header("Flow")]
    [Tooltip("Scene loaded after the rest baseline completes.")]
    public string nextScene = "02_Tutorial";
    [Tooltip("Seconds of eyes-open rest used to establish the resting baseline.")]
    public float restSeconds = 20f;

    [Header("UI (optional — wired by the scene builder)")]
    public GameObject explanationPanel;   // purpose text + Okay button
    public Button     okayButton;
    public TMP_Text   countdownLabel;      // shown during the rest count

    [Header("Bridge handshake")]
    [Tooltip("Seconds to wait for the bridge to acknowledge a baseline command before " +
             "proceeding anyway (UDP path only; the on-device path is synchronous).")]
    public float ackTimeout = 2f;

    // Resolve the baseline-control path: on-device direct adapter (Quest BLE) takes
    // precedence, else the UDP bridge adapter (PC over WiFi). Null = no EEG (Editor/desktop)
    // and every baseline call simply no-ops.
    private IMuseBaselineControl _baseline;

    private void Start()
    {
        if (MuseDirectAdapter.Instance != null) _baseline = MuseDirectAdapter.Instance;
        else                                    _baseline = MuseUdpAdapter.Instance;

        if (countdownLabel != null)   countdownLabel.gameObject.SetActive(false);
        if (explanationPanel != null) explanationPanel.SetActive(true);
        if (okayButton != null)       okayButton.onClick.AddListener(BeginRest);
    }

    private void BeginRest()
    {
        if (okayButton != null) okayButton.interactable = false;
        StartCoroutine(RestRoutine());
    }

    private IEnumerator RestRoutine()
    {
        if (explanationPanel != null) explanationPanel.SetActive(false);
        if (countdownLabel != null)
        {
            countdownLabel.gameObject.SetActive(true);
            countdownLabel.text = "Preparing the sensor...";
        }

        // Tell the bridge to start capturing the rest window, and wait until it confirms it
        // actually entered that phase — so the visible countdown lines up with what's recorded.
        _baseline?.StartRestBaseline();
        yield return WaitForAck(MuseUdpAdapter.CmdRestStart);

        float t = restSeconds;
        while (t > 0f)
        {
            if (countdownLabel != null)
                countdownLabel.text = $"Relax, eyes open...  {Mathf.CeilToInt(t)}s";
            t -= Time.deltaTime;
            yield return null;
        }

        // Commit the rest reference. The on-device adapter has a dedicated finalize; the UDP
        // bridge just stops the rest phase (rest is informational — the active-VR baseline is
        // the in-game reference).
        if (_baseline is MuseDirectAdapter direct)
        {
            if (!direct.FinalizeRestBaseline())
                Debug.LogWarning("[IntroController] rest baseline had no stable channel (poor contact?).");
        }
        else
        {
            _baseline?.StopRestBaseline();
            yield return WaitForAck(MuseUdpAdapter.CmdRestStop);
        }

        if (countdownLabel != null) countdownLabel.text = "Done - entering the tutorial...";
        yield return new WaitForSeconds(1.5f);
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
            if (udp.ConsumeAck(cmd)) { Debug.Log($"[IntroController] bridge acked '{cmd}'."); yield break; }
            t += Time.deltaTime;
            yield return null;
        }
        Debug.LogWarning($"[IntroController] no '{cmd}' ack within {ackTimeout}s — proceeding anyway.");
    }
}
