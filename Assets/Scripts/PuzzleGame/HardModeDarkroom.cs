using UnityEngine;
using UnityEngine.Rendering;

/// On the dark difficulties (MEDIUM and HARD), plunges the puzzle room into darkness (room
/// lights off, ambient dropped) and switches on the grabbable lantern — the player must grab
/// the lantern (it clips to their forearm) to light up and find the scattered pieces. Easy keeps
/// the room lit and the lantern hidden. The obstacle group is shown only on HARD.
///
/// Wired by the scene builder: subscribes to PuzzleManager.OnPuzzleStarted.
public class HardModeDarkroom : MonoBehaviour
{
    [Tooltip("Room lights switched off in the dark (medium/hard) mode.")]
    public Light[] roomLights;

    [Tooltip("The forearm lantern — enabled in the dark (medium/hard) mode.")]
    public GameObject lantern;

    [Tooltip("Obstacle group (walls / columns / baskets) — shown only on HARD.")]
    public GameObject obstacleRoot;

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
        if (obstacleRoot != null) obstacleRoot.SetActive(false);
    }

    private void OnDisable()
    {
        if (puzzleManager != null) puzzleManager.OnPuzzleStarted -= HandlePuzzleStarted;
    }

    private void HandlePuzzleStarted(DifficultyLevel level)
    {
        SetDark(level != DifficultyLevel.Easy);                       // Medium + Hard are dark
        if (obstacleRoot != null) obstacleRoot.SetActive(level == DifficultyLevel.Hard);
    }

    public void SetDark(bool dark)
    {
        if (roomLights != null)
            foreach (var l in roomLights)
                if (l != null) l.enabled = !dark;

        // Kill EVERY ambient/indirect source, not just the ambient colour — otherwise URP
        // environment reflections + ambient intensity still reveal wall/object outlines.
        RenderSettings.ambientMode      = AmbientMode.Flat;
        RenderSettings.ambientLight     = dark ? darkAmbient : litAmbient;
        RenderSettings.ambientIntensity = dark ? 0f : 1f;
        RenderSettings.reflectionIntensity = dark ? 0f : 1f;

        if (lantern != null) lantern.SetActive(dark);
    }
}
