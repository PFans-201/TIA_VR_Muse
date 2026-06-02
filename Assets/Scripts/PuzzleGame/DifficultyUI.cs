using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// Two-step world-space UI for selecting puzzle and difficulty.
///
/// Step 1 — Puzzle Type panel:  "Snowman"  |  "Robot"
/// Step 2 — Difficulty panel:   "Easy"  |  "Medium"  |  "Hard"
///
/// After both choices are confirmed, DifficultyUI calls
/// PuzzleManager.StartPuzzle(type, level) and hides itself.
public class DifficultyUI : MonoBehaviour
{
    [Header("Step 1 — Puzzle Type")]
    [Tooltip("Panel shown first, asking which puzzle to assemble")]
    public GameObject puzzleTypePanel;
    public Button     snowmanButton;
    public Button     robotButton;

    [Header("Step 2 — Difficulty")]
    [Tooltip("Panel shown after puzzle type is chosen")]
    public GameObject difficultyPanel;
    public Button     easyButton;
    public Button     mediumButton;
    public Button     hardButton;

    [Header("Status label (shared between both panels)")]
    [Tooltip("Shows the current selection state below the buttons")]
    public TextMeshProUGUI statusLabel;

    [Header("Puzzle Manager")]
    public PuzzleManager puzzleManager;

    private PuzzleType _selectedType;

    private void Start()
    {
        if (snowmanButton != null) snowmanButton.onClick.AddListener(OnSnowmanSelected);
        if (robotButton   != null) robotButton.onClick.AddListener(OnRobotSelected);
        if (easyButton    != null) easyButton.onClick.AddListener(() => ConfirmDifficulty(DifficultyLevel.Easy));
        if (mediumButton  != null) mediumButton.onClick.AddListener(() => ConfirmDifficulty(DifficultyLevel.Medium));
        if (hardButton    != null) hardButton.onClick.AddListener(() => ConfirmDifficulty(DifficultyLevel.Hard));

        ShowPuzzleTypeStep();
    }

    private void OnSnowmanSelected() => ShowDifficultyStep(PuzzleType.Snowman, "Snowman");
    private void OnRobotSelected()   => ShowDifficultyStep(PuzzleType.Robot,   "Robot");

    private void ShowPuzzleTypeStep()
    {
        if (puzzleTypePanel != null) puzzleTypePanel.SetActive(true);
        if (difficultyPanel != null) difficultyPanel.SetActive(false);
        if (statusLabel     != null) statusLabel.text = "Choose a puzzle to assemble";
    }

    private void ShowDifficultyStep(PuzzleType type, string typeName)
    {
        _selectedType = type;
        if (puzzleTypePanel != null) puzzleTypePanel.SetActive(false);
        if (difficultyPanel != null) difficultyPanel.SetActive(true);
        if (statusLabel     != null) statusLabel.text = $"Puzzle: {typeName}";
    }

    private void ConfirmDifficulty(DifficultyLevel level)
    {
        if (statusLabel != null)
            statusLabel.text = $"{_selectedType}  ·  {level}";

        if (puzzleManager != null)
            puzzleManager.StartPuzzle(_selectedType, level);
        else
            Debug.LogError("[DifficultyUI] PuzzleManager reference is missing!");

        gameObject.SetActive(false);
    }
}
