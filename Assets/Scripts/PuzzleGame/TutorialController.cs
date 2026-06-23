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

    private static MuseDirectAdapter Muse => MuseDirectAdapter.Instance;
    private bool _canProceed;

    private void Start()
    {
        if (proceedPanel != null)   proceedPanel.SetActive(false);
        if (continueButton != null) continueButton.onClick.AddListener(Proceed);

        Muse?.StartTutorialBaseline();   // stream vs rest + collect active reference
        StartCoroutine(MinTimer());
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
        Muse?.FinalizeBaseline();   // commits the active-VR reference (logs if no stable channel)
        SceneManager.LoadScene(nextScene);
    }
}
