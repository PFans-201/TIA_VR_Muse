using UnityEngine;

/// On HARD difficulty, plunges the puzzle room into darkness (room lights off, ambient
/// dropped) and switches on the grabbable lantern — the player must grab the lantern
/// (it clips to their forearm) to light up and find the scattered pieces. Any other
/// difficulty keeps the room lit and the lantern hidden.
///
/// Wired by the scene builder: subscribes to PuzzleManager.OnPuzzleStarted.
public class HardModeDarkroom : MonoBehaviour
{
    [Tooltip("Room lights switched off in the dark (hard) mode.")]
    public Light[] roomLights;

    [Tooltip("The forearm lantern — enabled only in the dark (hard) mode.")]
    public GameObject lantern;

    [Tooltip("PuzzleManager whose OnPuzzleStarted drives the dark/lit toggle.")]
    public PuzzleManager puzzleManager;

    [Tooltip("Ambient colour while lit (restored when leaving hard mode).")]
    public Color litAmbient = new Color(0.32f, 0.32f, 0.32f);

    [Tooltip("Ambient colour in the dark — pure black so nothing but the lantern is visible.")]
    public Color darkAmbient = Color.black;

    private void OnEnable()
    {
        if (puzzleManager != null) puzzleManager.OnPuzzleStarted += HandlePuzzleStarted;
        SetDark(false);   // start lit until a puzzle is chosen
    }

    private void OnDisable()
    {
        if (puzzleManager != null) puzzleManager.OnPuzzleStarted -= HandlePuzzleStarted;
    }

    private void HandlePuzzleStarted(PuzzleType type, DifficultyLevel level)
        => SetDark(level == DifficultyLevel.Hard);

    public void SetDark(bool dark)
    {
        if (roomLights != null)
            foreach (var l in roomLights)
                if (l != null) l.enabled = !dark;

        RenderSettings.ambientLight = dark ? darkAmbient : litAmbient;

        if (lantern != null)
        {
            if (dark) lantern.SetActive(true);
            else      lantern.SetActive(false);
        }
    }
}
