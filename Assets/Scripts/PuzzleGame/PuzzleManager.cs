using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Central controller for the puzzle game. Lives in the Zen scene.
/// 
/// How puzzle pieces work:
///   - You create a 3D object (e.g. a bear model or simple geometry).
///   - You manually break it into child GameObjects, each with a PuzzlePiece component.
///   - All children are assigned to the "puzzlePieceGroups" list below.
///   - At runtime, PuzzleManager activates the right number of pieces per difficulty
///     and configures magnetism.
/// 
/// Setup in Unity Editor:
///   1. Create an empty GameObject "PuzzleManager" in the Zen scene and attach this script.
///   2. Create your 3D puzzle object with child pieces (see PuzzlePiece.cs).
///   3. Populate "easyPieces", "mediumPieces", "hardPieces" lists with piece GameObjects.
///   4. Assign the "puzzleAnchor" (where the completed puzzle should sit).
///   5. Assign "difficultyUI" reference.
/// </summary>
public class PuzzleManager : MonoBehaviour
{
    [Header("Puzzle Piece Sets")]
    [Tooltip("Pieces active on Easy — fewest, largest pieces")]
    public List<GameObject> easyPieces;

    [Tooltip("Pieces active on Medium")]
    public List<GameObject> mediumPieces;

    [Tooltip("Pieces active on Hard — most, smallest pieces")]
    public List<GameObject> hardPieces;

    [Header("Settings")]
    [Tooltip("Should Easy mode enable magnetic snapping?")]
    public bool easyMagnetic = true;

    [Tooltip("Should Medium mode enable magnetic snapping?")]
    public bool mediumMagnetic = false;

    [Tooltip("Should Hard mode enable magnetic snapping?")]
    public bool hardMagnetic = false;

    [Tooltip("Where the completed puzzle should be displayed")]
    public Transform puzzleAnchor;

    [Header("State")]
    [SerializeField, Tooltip("Current difficulty (read-only at runtime)")]
    private DifficultyLevel _currentDifficulty;

    private List<GameObject> _activePieces = new();
    private int _solvedCount = 0;

    /// <summary>Called by DifficultyUI when the player picks a difficulty.</summary>
    public void StartPuzzle(DifficultyLevel level)
    {
        _currentDifficulty = level;
        _solvedCount = 0;

        // Hide all piece sets first
        SetGroupActive(easyPieces,   false);
        SetGroupActive(mediumPieces, false);
        SetGroupActive(hardPieces,   false);

        // Activate the correct set and configure magnetism
        bool useMagnet;
        switch (level)
        {
            case DifficultyLevel.Easy:
                _activePieces = easyPieces;
                useMagnet = easyMagnetic;
                break;
            case DifficultyLevel.Medium:
                _activePieces = mediumPieces;
                useMagnet = mediumMagnetic;
                break;
            default: // Hard
                _activePieces = hardPieces;
                useMagnet = hardMagnetic;
                break;
        }

        foreach (var pieceObj in _activePieces)
        {
            if (pieceObj == null) continue;
            pieceObj.SetActive(true);

            // Scatter pieces around the puzzle anchor
            if (puzzleAnchor != null)
                pieceObj.transform.position = puzzleAnchor.position + Random.insideUnitSphere * 0.5f;

            // Configure each PuzzlePiece component
            var piece = pieceObj.GetComponent<PuzzlePiece>();
            if (piece != null)
            {
                piece.isMagneticEnabled = useMagnet;
                piece.OnPieceSolved += HandlePieceSolved;
            }
        }

        Debug.Log($"[PuzzleManager] Started puzzle — Difficulty: {level}, Pieces: {_activePieces.Count}, Magnetic: {useMagnet}");
    }

    private void HandlePieceSolved()
    {
        _solvedCount++;
        Debug.Log($"[PuzzleManager] Pieces solved: {_solvedCount} / {_activePieces.Count}");

        if (_solvedCount >= _activePieces.Count)
            OnPuzzleComplete();
    }

    private void OnPuzzleComplete()
    {
        Debug.Log("[PuzzleManager] Puzzle complete! Well done!");
        // TODO: Trigger celebration particle effects, sound, or a "Congratulations" UI panel here.
    }

    private void SetGroupActive(List<GameObject> group, bool active)
    {
        foreach (var obj in group)
            if (obj != null) obj.SetActive(active);
    }
}
