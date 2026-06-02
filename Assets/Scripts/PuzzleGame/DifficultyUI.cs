using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Manages the difficulty questionnaire shown before the puzzle starts.
/// 
/// Setup in Unity Editor:
///   1. Create a Canvas (World Space) in the Zen scene.
///   2. Add 3 Buttons: Easy, Medium, Hard (or use the slider variant below).
///   3. Attach this script to the Canvas.
///   4. Wire up the Button.onClick events to OnEasySelected(), OnMediumSelected(), OnHardSelected().
///   5. Assign the "puzzleManager" reference in the Inspector.
/// 
/// Alternatively, assign the three buttons directly in the Inspector fields below.
/// </summary>
public class DifficultyUI : MonoBehaviour
{
    [Header("UI References")]
    [Tooltip("Button the player clicks/pokes for Easy mode")]
    public Button easyButton;

    [Tooltip("Button for Medium mode")]
    public Button mediumButton;

    [Tooltip("Button for Hard mode")]
    public Button hardButton;

    [Tooltip("Text label that shows the currently selected difficulty")]
    public TextMeshProUGUI selectedDifficultyLabel;

    [Tooltip("Panel shown during the questionnaire (will be hidden after selection)")]
    public GameObject questionnairePanel;

    [Header("Puzzle Manager")]
    [Tooltip("Reference to the PuzzleManager in the scene")]
    public PuzzleManager puzzleManager;

    private void Start()
    {
        // Wire up buttons in code as a safety net (can also wire in Inspector)
        if (easyButton != null)   easyButton.onClick.AddListener(OnEasySelected);
        if (mediumButton != null) mediumButton.onClick.AddListener(OnMediumSelected);
        if (hardButton != null)   hardButton.onClick.AddListener(OnHardSelected);

        if (questionnairePanel != null)
            questionnairePanel.SetActive(true);
    }

    public void OnEasySelected()   => ConfirmDifficulty(DifficultyLevel.Easy);
    public void OnMediumSelected() => ConfirmDifficulty(DifficultyLevel.Medium);
    public void OnHardSelected()   => ConfirmDifficulty(DifficultyLevel.Hard);

    private void ConfirmDifficulty(DifficultyLevel level)
    {
        if (selectedDifficultyLabel != null)
            selectedDifficultyLabel.text = $"Difficulty: {level}";

        if (puzzleManager != null)
            puzzleManager.StartPuzzle(level);
        else
            Debug.LogError("[DifficultyUI] PuzzleManager reference is missing!");

        // Hide the questionnaire panel after selection
        if (questionnairePanel != null)
            questionnairePanel.SetActive(false);
    }
}
