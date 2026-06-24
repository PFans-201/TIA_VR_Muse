using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// Single-step world-space UI for the robot puzzle: choose a difficulty.
///
/// ProgressionMode.FreeChoice (default)
///   All difficulties are available immediately. Difficulty expresses how much baseline
///   assistance the player wants; the adaptive system adjusts further from MUSE S readings.
///
/// ProgressionMode.Sequential
///   Easy is always available. Medium unlocks once Easy is completed, Hard once Medium is.
///   Locked buttons are dimmed and non-interactable. After each completion the menu re-opens.
///
/// An info (ⓘ) button toggles a panel describing the three difficulty "maps".
public class DifficultyUI : MonoBehaviour
{
    [Header("Progression")]
    [Tooltip("FreeChoice (default): all difficulties selectable immediately.\n" +
             "Sequential: Easy must be completed before Medium unlocks, etc.")]
    public ProgressionMode progressionMode = ProgressionMode.FreeChoice;

    [Header("Difficulty")]
    public GameObject difficultyPanel;
    public Button     easyButton;
    public Button     mediumButton;
    public Button     hardButton;

    [Header("Per-mode Info (ⓘ badges)")]
    public Button     easyInfoButton;
    public Button     mediumInfoButton;
    public Button     hardInfoButton;
    public Button     infoCloseButton;
    public GameObject infoPanel;
    public TextMeshProUGUI infoTitle;
    public TextMeshProUGUI infoBody;

    [Header("Per-mode Info content (set by the scene builder)")]
    [TextArea] public string easyInfoTitle;
    [TextArea] public string easyInfoBody;
    [TextArea] public string mediumInfoTitle;
    [TextArea] public string mediumInfoBody;
    [TextArea] public string hardInfoTitle;
    [TextArea] public string hardInfoBody;

    [Header("Labels")]
    public TextMeshProUGUI statusLabel;

    [Header("Puzzle Manager")]
    public PuzzleManager puzzleManager;

    // ── Runtime state ─────────────────────────────────────────────────────────

    // Highest unlocked difficulty this session (Sequential mode only).
    private DifficultyLevel _maxUnlocked = DifficultyLevel.Easy;

    private Color _easyOrigColor;
    private Color _mediumOrigColor;
    private Color _hardOrigColor;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Start()
    {
        if (easyButton   != null) easyButton.onClick.AddListener(()   => ConfirmDifficulty(DifficultyLevel.Easy));
        if (mediumButton != null) mediumButton.onClick.AddListener(() => ConfirmDifficulty(DifficultyLevel.Medium));
        if (hardButton   != null) hardButton.onClick.AddListener(()   => ConfirmDifficulty(DifficultyLevel.Hard));
        if (easyInfoButton   != null) easyInfoButton.onClick.AddListener(()   => ShowInfo(DifficultyLevel.Easy));
        if (mediumInfoButton != null) mediumInfoButton.onClick.AddListener(() => ShowInfo(DifficultyLevel.Medium));
        if (hardInfoButton   != null) hardInfoButton.onClick.AddListener(()   => ShowInfo(DifficultyLevel.Hard));
        if (infoCloseButton  != null) infoCloseButton.onClick.AddListener(CloseInfo);

        _easyOrigColor   = GetButtonColor(easyButton);
        _mediumOrigColor = GetButtonColor(mediumButton);
        _hardOrigColor   = GetButtonColor(hardButton);

        if (puzzleManager != null)
            puzzleManager.OnPuzzleCompleted += HandlePuzzleCompleted;

        if (infoPanel != null) infoPanel.SetActive(false);
        ShowDifficultyStep();
    }

    private void OnDestroy()
    {
        if (puzzleManager != null)
            puzzleManager.OnPuzzleCompleted -= HandlePuzzleCompleted;
    }

    // ── Difficulty selection ──────────────────────────────────────────────────

    private void ShowDifficultyStep()
    {
        if (difficultyPanel != null) difficultyPanel.SetActive(true);
        if (statusLabel     != null) statusLabel.text = "Choose a difficulty";
        RefreshDifficultyButtons();
    }

    private void RefreshDifficultyButtons()
    {
        if (progressionMode == ProgressionMode.FreeChoice)
        {
            SetButtonLocked(easyButton,   _easyOrigColor,   locked: false);
            SetButtonLocked(mediumButton, _mediumOrigColor, locked: false);
            SetButtonLocked(hardButton,   _hardOrigColor,   locked: false);
            return;
        }

        SetButtonLocked(easyButton,   _easyOrigColor,   locked: DifficultyLevel.Easy   > _maxUnlocked);
        SetButtonLocked(mediumButton, _mediumOrigColor, locked: DifficultyLevel.Medium > _maxUnlocked);
        SetButtonLocked(hardButton,   _hardOrigColor,   locked: DifficultyLevel.Hard   > _maxUnlocked);
    }

    private void ConfirmDifficulty(DifficultyLevel level)
    {
        if (statusLabel != null) statusLabel.text = $"Robot  ·  {level}";

        if (puzzleManager != null)
            puzzleManager.StartPuzzle(level);
        else
            Debug.LogError("[DifficultyUI] PuzzleManager reference is missing!");

        gameObject.SetActive(false);
    }

    // ── Per-mode info ───────────────────────────────────────────────────────────

    private void ShowInfo(DifficultyLevel level)
    {
        (string title, string body) = level switch
        {
            DifficultyLevel.Easy   => (easyInfoTitle,   easyInfoBody),
            DifficultyLevel.Medium => (mediumInfoTitle, mediumInfoBody),
            _                      => (hardInfoTitle,   hardInfoBody),
        };
        if (infoTitle != null) infoTitle.text = title;
        if (infoBody  != null) infoBody.text  = body;
        if (infoPanel != null) infoPanel.SetActive(true);
    }

    private void CloseInfo()
    {
        if (infoPanel != null) infoPanel.SetActive(false);
    }

    // ── Puzzle completion callback ────────────────────────────────────────────

    private void HandlePuzzleCompleted(DifficultyLevel completedLevel)
    {
        DifficultyLevel next = completedLevel switch
        {
            DifficultyLevel.Easy   => DifficultyLevel.Medium,
            DifficultyLevel.Medium => DifficultyLevel.Hard,
            _                      => DifficultyLevel.Hard,
        };
        if (_maxUnlocked < next) _maxUnlocked = next;

        gameObject.SetActive(true);
        if (infoPanel != null) infoPanel.SetActive(false);
        ShowDifficultyStep();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Color GetButtonColor(Button btn)
    {
        if (btn == null) return Color.white;
        var img = btn.GetComponent<Image>();
        return img != null ? img.color : Color.white;
    }

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

        var label = btn.GetComponentInChildren<TextMeshProUGUI>();
        if (label != null)
            label.color = locked ? new Color(1f, 1f, 1f, 0.35f) : Color.white;
    }
}
