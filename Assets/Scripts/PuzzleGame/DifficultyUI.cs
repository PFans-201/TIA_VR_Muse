using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// Two-step world-space UI: choose a puzzle, then choose a difficulty.
///
/// ProgressionMode.Sequential (default)
///   Easy is always available. Medium unlocks once Easy is completed.
///   Hard unlocks once Medium is completed. Locked buttons are dimmed
///   and non-interactable. After each puzzle completion the UI re-opens
///   automatically so the player can continue to the next level.
///
/// ProgressionMode.FreeChoice
///   All difficulties are available immediately — useful for testing
///   or when the session protocol does not require a fixed order.
public class DifficultyUI : MonoBehaviour
{
    [Header("Progression")]
    [Tooltip("Sequential: Easy → Medium → Hard unlocks progressively.\n" +
             "FreeChoice: all difficulties available from the start.")]
    [Tooltip("FreeChoice (default): all difficulties selectable immediately — difficulty expresses\n" +
             "how much baseline assistance the player wants. The adaptive system will adjust\n" +
             "further based on MUSE S readings regardless of this initial choice.\n\n" +
             "Sequential: Easy must be completed before Medium unlocks, etc.\n" +
             "Useful if a fixed progression order is required by the study protocol.")]
    public ProgressionMode progressionMode = ProgressionMode.FreeChoice;

    [Header("Step 1 — Puzzle Type")]
    public GameObject puzzleTypePanel;
    public Button     snowmanButton;
    public Button     robotButton;

    [Header("Step 2 — Difficulty")]
    public GameObject difficultyPanel;
    public Button     easyButton;
    public Button     mediumButton;
    public Button     hardButton;

    [Header("Labels")]
    public TextMeshProUGUI statusLabel;

    [Header("Puzzle Manager")]
    public PuzzleManager puzzleManager;

    // ── Runtime state ─────────────────────────────────────────────────────────

    private PuzzleType _selectedType;

    // Tracks the highest unlocked DifficultyLevel per puzzle type this session.
    // Starts at Easy for every type; advances as puzzles are completed.
    private readonly Dictionary<PuzzleType, DifficultyLevel> _maxUnlocked = new()
    {
        { PuzzleType.Snowman, DifficultyLevel.Easy },
        { PuzzleType.Robot,   DifficultyLevel.Easy },
    };

    // Original Image colours for the difficulty buttons (stored so we can dim/restore them)
    private Color _easyOrigColor;
    private Color _mediumOrigColor;
    private Color _hardOrigColor;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Start()
    {
        // Wire button callbacks
        if (snowmanButton != null) snowmanButton.onClick.AddListener(OnSnowmanSelected);
        if (robotButton   != null) robotButton.onClick.AddListener(OnRobotSelected);
        if (easyButton    != null) easyButton.onClick.AddListener(() => ConfirmDifficulty(DifficultyLevel.Easy));
        if (mediumButton  != null) mediumButton.onClick.AddListener(() => ConfirmDifficulty(DifficultyLevel.Medium));
        if (hardButton    != null) hardButton.onClick.AddListener(() => ConfirmDifficulty(DifficultyLevel.Hard));

        // Cache original button colours before any dimming
        _easyOrigColor   = GetButtonColor(easyButton);
        _mediumOrigColor = GetButtonColor(mediumButton);
        _hardOrigColor   = GetButtonColor(hardButton);

        // Listen for puzzle completion so we can unlock the next difficulty
        if (puzzleManager != null)
            puzzleManager.OnPuzzleCompleted += HandlePuzzleCompleted;

        ShowPuzzleTypeStep();
    }

    private void OnDestroy()
    {
        if (puzzleManager != null)
            puzzleManager.OnPuzzleCompleted -= HandlePuzzleCompleted;
    }

    // ── Step 1: puzzle type selection ─────────────────────────────────────────

    private void OnSnowmanSelected() => ShowDifficultyStep(PuzzleType.Snowman, "Snowman");
    private void OnRobotSelected()   => ShowDifficultyStep(PuzzleType.Robot,   "Robot");

    private void ShowPuzzleTypeStep()
    {
        if (puzzleTypePanel != null) puzzleTypePanel.SetActive(true);
        if (difficultyPanel != null) difficultyPanel.SetActive(false);
        if (statusLabel     != null) statusLabel.text = "Choose a puzzle to assemble";
    }

    // ── Step 2: difficulty selection ──────────────────────────────────────────

    private void ShowDifficultyStep(PuzzleType type, string typeName)
    {
        _selectedType = type;
        if (puzzleTypePanel != null) puzzleTypePanel.SetActive(false);
        if (difficultyPanel != null) difficultyPanel.SetActive(true);
        if (statusLabel     != null) statusLabel.text = $"Puzzle: {typeName}";

        RefreshDifficultyButtons();
    }

    /// Updates button interactability and tint based on the current unlock state.
    private void RefreshDifficultyButtons()
    {
        if (progressionMode == ProgressionMode.FreeChoice)
        {
            SetButtonLocked(easyButton,   _easyOrigColor,   locked: false);
            SetButtonLocked(mediumButton, _mediumOrigColor, locked: false);
            SetButtonLocked(hardButton,   _hardOrigColor,   locked: false);
            return;
        }

        DifficultyLevel max = _maxUnlocked.TryGetValue(_selectedType, out var m) ? m : DifficultyLevel.Easy;

        SetButtonLocked(easyButton,   _easyOrigColor,   locked: DifficultyLevel.Easy   > max);
        SetButtonLocked(mediumButton, _mediumOrigColor, locked: DifficultyLevel.Medium > max);
        SetButtonLocked(hardButton,   _hardOrigColor,   locked: DifficultyLevel.Hard   > max);
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

    // ── Puzzle completion callback ────────────────────────────────────────────

    private void HandlePuzzleCompleted(PuzzleType type, DifficultyLevel completedLevel)
    {
        // Advance the unlock ceiling for this puzzle type
        DifficultyLevel next = completedLevel switch
        {
            DifficultyLevel.Easy   => DifficultyLevel.Medium,
            DifficultyLevel.Medium => DifficultyLevel.Hard,
            _                     => DifficultyLevel.Hard,
        };

        if (!_maxUnlocked.ContainsKey(type) || _maxUnlocked[type] < next)
            _maxUnlocked[type] = next;

        // Re-open the UI so the player can continue (the event fires even on an inactive GO)
        gameObject.SetActive(true);
        ShowPuzzleTypeStep();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Color GetButtonColor(Button btn)
    {
        if (btn == null) return Color.white;
        var img = btn.GetComponent<Image>();
        return img != null ? img.color : Color.white;
    }

    /// Dims and disables a button (locked) or restores it to its original colour (unlocked).
    private static void SetButtonLocked(Button btn, Color originalColor, bool locked)
    {
        if (btn == null) return;
        btn.interactable = !locked;
        var img = btn.GetComponent<Image>();
        if (img != null)
            img.color = locked
                ? new Color(originalColor.r * 0.45f, originalColor.g * 0.45f,
                            originalColor.b * 0.45f, 0.40f)
                : originalColor;

        // Also dim the label text so it reads as unavailable
        var label = btn.GetComponentInChildren<TextMeshProUGUI>();
        if (label != null)
            label.color = locked ? new Color(1f, 1f, 1f, 0.35f) : Color.white;
    }
}
