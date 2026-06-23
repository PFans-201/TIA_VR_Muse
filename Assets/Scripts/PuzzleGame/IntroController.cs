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

    private static MuseDirectAdapter Muse => MuseDirectAdapter.Instance;

    private void Start()
    {
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
        if (countdownLabel != null)   countdownLabel.gameObject.SetActive(true);

        Muse?.StartRestBaseline();

        float t = restSeconds;
        while (t > 0f)
        {
            if (countdownLabel != null)
                countdownLabel.text = $"Relax, eyes open...  {Mathf.CeilToInt(t)}s";
            t -= Time.deltaTime;
            yield return null;
        }

        if (Muse != null && !Muse.FinalizeRestBaseline())
            Debug.LogWarning("[IntroController] rest baseline had no stable channel (poor contact?).");

        if (countdownLabel != null) countdownLabel.text = "Done - entering the tutorial...";
        yield return new WaitForSeconds(1.5f);
        SceneManager.LoadScene(nextScene);
    }
}
