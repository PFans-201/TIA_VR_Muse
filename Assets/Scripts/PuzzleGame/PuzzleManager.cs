using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// Central controller for both puzzle types and all three difficulty levels.
///
/// Difficulty affects three independent axes:
///   Magnetic force  — Easy: strong pull from far away / Hard: none
///   Piece colour    — Easy: full colour  / Hard: bleeds into the zen grey background
///   Ghost alpha     — Easy: clearly visible silhouettes / Hard: barely perceptible
///   Scatter radius  — Easy: pieces stay near centre of table / Hard: scattered wide
public class PuzzleManager : MonoBehaviour
{
    // ── Difficulty settings struct ────────────────────────────────────────────

    [Serializable]
    public struct DifficultySettings
    {
        [Tooltip("Force (Acceleration mode) pulling a held-but-near piece towards its zone")]
        public float magnetForce;
        [Tooltip("Distance (m) from the snap zone that activates the magnetic pull")]
        public float magnetRange;
        [Tooltip("Radius (m) used when scattering pieces from the puzzle anchor on start")]
        public float scatterRadius;
        [Tooltip("1 = full piece colour  |  0 = same grey as the zen wall (invisible)")]
        [Range(0f, 1f)] public float pieceBrightness;
        [Tooltip("Ghost silhouette alpha when the piece is far from its zone")]
        [Range(0f, 1f)] public float ghostIdleAlpha;
        [Tooltip("Ghost silhouette alpha when the piece is close to its zone")]
        [Range(0f, 1f)] public float ghostActiveAlpha;
    }

    // ── Snowman puzzle (Simple) ───────────────────────────────────────────────

    [Header("Snowman Puzzle — Simple")]
    [Tooltip("3 pieces: Body, Head, Hat")]
    public List<GameObject> snowmanEasyPieces   = new();
    [Tooltip("5 pieces: + LeftArm, RightArm")]
    public List<GameObject> snowmanMediumPieces = new();
    [Tooltip("7 pieces: + LeftLeg, RightLeg  (also used by HideAll)")]
    public List<GameObject> snowmanHardPieces   = new();

    // ── Robot puzzle (Complex) ────────────────────────────────────────────────

    [Header("Robot Puzzle — Complex")]
    [Tooltip("5 pieces: Head, Torso, LeftArm, RightArm, LeftLeg")]
    public List<GameObject> robotEasyPieces     = new();
    [Tooltip("8 pieces: + RightLeg, LeftForearm, RightForearm")]
    public List<GameObject> robotMediumPieces   = new();
    [Tooltip("12 pieces: + LeftFoot, RightFoot, LeftEye, RightEye  (also used by HideAll)")]
    public List<GameObject> robotHardPieces     = new();

    // ── Difficulty settings ───────────────────────────────────────────────────

    [Header("Difficulty Settings")]
    public DifficultySettings easySettings = new DifficultySettings
    {
        magnetForce = 12f, magnetRange = 0.35f, scatterRadius = 0.35f,
        pieceBrightness = 1.00f, ghostIdleAlpha = 0.45f, ghostActiveAlpha = 0.70f
    };
    public DifficultySettings mediumSettings = new DifficultySettings
    {
        magnetForce = 4f, magnetRange = 0.12f, scatterRadius = 0.65f,
        pieceBrightness = 0.60f, ghostIdleAlpha = 0.20f, ghostActiveAlpha = 0.40f
    };
    public DifficultySettings hardSettings = new DifficultySettings
    {
        magnetForce = 0f, magnetRange = 0.00f, scatterRadius = 1.10f,
        pieceBrightness = 0.25f, ghostIdleAlpha = 0.04f, ghostActiveAlpha = 0.12f
    };

    // ── References ────────────────────────────────────────────────────────────

    [Header("References")]
    [Tooltip("Pieces are scattered around this point when a puzzle starts")]
    public Transform       puzzleAnchor;
    [Tooltip("Optional — wired by the scene builder; manages MUSE S hint colours")]
    public PieceHintSystem hintSystem;

    // ── Runtime state ─────────────────────────────────────────────────────────

    [Header("Runtime State (read-only)")]
    [SerializeField] private DifficultyLevel _currentDifficulty;
    [SerializeField] private PuzzleType      _currentPuzzleType;

    private List<GameObject> _activePieces = new();
    private int              _solvedCount;

    /// Fired when the player completes a puzzle — DifficultyUI listens to unlock the next level.
    public event Action<PuzzleType, DifficultyLevel> OnPuzzleCompleted;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        // Hide every piece and snap zone immediately — before the first frame renders.
        // This fixes the issue where pieces were visible before difficulty selection.
        SetGroupActive(snowmanHardPieces, false);
        SetGroupActive(robotHardPieces,   false);
        HideAllSnapZones();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// Called by DifficultyUI when the player confirms puzzle type and difficulty.
    public void StartPuzzle(PuzzleType puzzleType, DifficultyLevel level)
    {
        // Unsubscribe previous listeners before overwriting _activePieces
        foreach (var obj in _activePieces)
        {
            var pp = obj?.GetComponent<PuzzlePiece>();
            if (pp != null) pp.OnPieceSolved -= HandlePieceSolved;
        }

        _currentPuzzleType = puzzleType;
        _currentDifficulty = level;
        _solvedCount       = 0;

        // Deactivate every piece and snap zone across both puzzles
        SetGroupActive(snowmanHardPieces, false);
        SetGroupActive(robotHardPieces,   false);
        HideAllSnapZones();

        // Pick the correct subset
        _activePieces = (puzzleType, level) switch
        {
            (PuzzleType.Snowman, DifficultyLevel.Easy)   => snowmanEasyPieces,
            (PuzzleType.Snowman, DifficultyLevel.Medium) => snowmanMediumPieces,
            (PuzzleType.Snowman, _)                      => snowmanHardPieces,
            (PuzzleType.Robot,   DifficultyLevel.Easy)   => robotEasyPieces,
            (PuzzleType.Robot,   DifficultyLevel.Medium) => robotMediumPieces,
            _                                            => robotHardPieces,
        };

        DifficultySettings s = level switch
        {
            DifficultyLevel.Easy   => easySettings,
            DifficultyLevel.Medium => mediumSettings,
            _                     => hardSettings,
        };

        var hintPairs = new List<PieceHintSystem.PiecePair>();

        foreach (var pieceObj in _activePieces)
        {
            if (pieceObj == null) continue;
            pieceObj.SetActive(true);

            // Scatter pieces on the table surface around the anchor
            if (puzzleAnchor != null)
            {
                Vector2 rnd = UnityEngine.Random.insideUnitCircle * s.scatterRadius;
                pieceObj.transform.position = puzzleAnchor.position +
                                              new Vector3(rnd.x, 0.20f, rnd.y);
            }

            var piece = pieceObj.GetComponent<PuzzlePiece>();
            if (piece == null) continue;

            piece.isMagneticEnabled = s.magnetForce > 0f;
            piece.magnetForce       = s.magnetForce;
            piece.magnetRange       = s.magnetRange;
            piece.OnPieceSolved    += HandlePieceSolved;
            piece.SetVisibility(s.pieceBrightness);

            // Activate the matching snap zone and configure it
            if (piece.correctPlacementTarget != null)
            {
                var snapGO = piece.correctPlacementTarget.gameObject;
                snapGO.SetActive(true);

                var zone = snapGO.GetComponent<MagneticSnapZone>();
                if (zone != null)
                {
                    // Snap zone activation range mirrors magnetic range (minimum 0.10 m)
                    zone.activationRange = Mathf.Max(s.magnetRange, 0.10f);
                    zone.SetGhostAlpha(s.ghostIdleAlpha, s.ghostActiveAlpha);
                    hintPairs.Add(new PieceHintSystem.PiecePair { piece = piece, snapZone = zone });
                }
            }
        }

        hintSystem?.RegisterPairs(hintPairs);

        Debug.Log($"[PuzzleManager] {puzzleType} · {level} — " +
                  $"{_activePieces.Count} pieces  " +
                  $"magnet={s.magnetForce:F1} N  " +
                  $"brightness={s.pieceBrightness * 100:F0}%");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void HandlePieceSolved()
    {
        _solvedCount++;
        Debug.Log($"[PuzzleManager] {_solvedCount}/{_activePieces.Count} pieces solved");
        if (_solvedCount >= _activePieces.Count)
            OnPuzzleComplete();
    }

    private void OnPuzzleComplete()
    {
        Debug.Log($"[PuzzleManager] Puzzle complete! ({_currentPuzzleType} · {_currentDifficulty})");
        // TODO: Trigger celebration FX — confetti particle system, completion sound,
        //       "Well done!" UI panel. The Confetti prefab in VRTemplateAssets/Prefabs
        //       can be instantiated here.
        OnPuzzleCompleted?.Invoke(_currentPuzzleType, _currentDifficulty);
    }

    private void SetGroupActive(List<GameObject> group, bool active)
    {
        foreach (var obj in group)
            if (obj != null) obj.SetActive(active);
    }

    private void HideAllSnapZones()
    {
        foreach (var pieceObj in AllPieceObjects())
        {
            if (pieceObj == null) continue;
            var piece = pieceObj.GetComponent<PuzzlePiece>();
            if (piece?.correctPlacementTarget != null)
                piece.correctPlacementTarget.gameObject.SetActive(false);
        }
    }

    // Iterates ALL pieces across both puzzle types (hard lists include every piece)
    private IEnumerable<GameObject> AllPieceObjects()
    {
        foreach (var p in snowmanHardPieces) yield return p;
        foreach (var p in robotHardPieces)   yield return p;
    }
}
