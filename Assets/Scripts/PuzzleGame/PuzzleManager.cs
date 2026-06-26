using System;
using System.Collections.Generic;
using UnityEngine;

/// Central controller for the (robot-only) puzzle across all three difficulty levels.
///
/// Difficulty differentiates several independent axes:
///   Piece count     — Easy 5 / Medium 12 / Hard 22
///   Magnetic pull   — Easy: strong, snaps even while held / Medium: moderate / Hard: weak
///   Piece colour    — Easy: full colour / Hard: bleeds into the zen grey background
///   Ghost alpha     — Easy: clear silhouettes / Hard: barely perceptible
///   Start position  — Easy: near solved slot / Medium: offset / Hard: dropped from the ceiling
///   Room / obstacles— Easy: lit, clear / Medium: dark / Hard: dark + obstacles
public class PuzzleManager : MonoBehaviour
{
    // ── Difficulty settings struct ────────────────────────────────────────────

    [Serializable]
    public struct DifficultySettings
    {
        [Tooltip("Number of robot pieces used (taken from the start of the robotPieces list)")]
        public int pieceCount;
        [Tooltip("Pull strength toward the slot (higher = snappier)")]
        public float magnetForce;
        [Tooltip("Distance (m) from the snap zone that activates the magnetic pull")]
        public float magnetRange;
        [Tooltip("Easy: also pull/snap the piece into its slot while it is still held")]
        public bool  magnetWhileHeld;
        [Tooltip("How pieces are positioned when the puzzle starts")]
        public SpawnMode spawnMode;
        [Tooltip("Offset radius (m) from each piece's solved slot for NearSolved / OffsetFromSolved")]
        public float startOffset;
        [Tooltip("1 = full piece colour  |  0 = same grey as the zen wall (invisible)")]
        [Range(0f, 1f)] public float pieceBrightness;
        [Tooltip("Ghost silhouette alpha when the piece is far from its zone")]
        [Range(0f, 1f)] public float ghostIdleAlpha;
        [Tooltip("Ghost silhouette alpha when the piece is close to its zone")]
        [Range(0f, 1f)] public float ghostActiveAlpha;
    }

    // ── Robot puzzle pieces ───────────────────────────────────────────────────

    [Header("Robot Puzzle")]
    [Tooltip("Legacy/shared ordered list. Used only for a difficulty that has no per-difficulty " +
             "set below — it then takes the first N (its pieceCount).")]
    public List<GameObject> robotPieces = new();

    [Header("Per-Difficulty Piece Sets (optional)")]
    [Tooltip("When a level's list is non-empty the WHOLE list is its puzzle (each from its own " +
             "prefab); piece count = list size, and robotPieces/pieceCount are ignored for it.")]
    public List<GameObject> easyPieces   = new();
    public List<GameObject> mediumPieces = new();
    public List<GameObject> hardPieces   = new();

    // ── Difficulty settings ───────────────────────────────────────────────────

    [Header("Difficulty Settings")]
    public DifficultySettings easySettings = new DifficultySettings
    {
        // startOffset (0.60) is deliberately MUCH larger than magnetRange so pieces never auto-snap
        // the moment the puzzle starts — the player must carry each piece into range. (PositionPiece
        // also enforces a minimum scatter radius above magnetRange, so even the nearest-scattered
        // piece starts clearly OUT of the magnet field — no autocomplete on spawn.)
        // magnetWhileHeld = FALSE so Easy uses the SAME place-on-release flow as Medium/Hard: the
        // piece is dropped near the slot and only then snaps home. With magnetWhileHeld the piece
        // snapped straight out of the hand into the assembly and read as "disappeared"; a generous
        // magnetRange keeps it forgiving without the vanish.
        pieceCount = 5,  magnetForce = 8f, magnetRange = 0.16f, magnetWhileHeld = false,
        spawnMode = SpawnMode.NearSolved, startOffset = 0.60f,
        pieceBrightness = 1.00f, ghostIdleAlpha = 0.55f, ghostActiveAlpha = 0.80f
    };
    public DifficultySettings mediumSettings = new DifficultySettings
    {
        pieceCount = 12, magnetForce = 5f,  magnetRange = 0.13f, magnetWhileHeld = false,
        spawnMode = SpawnMode.OffsetFromSolved, startOffset = 0.40f,
        pieceBrightness = 0.65f, ghostIdleAlpha = 0.22f, ghostActiveAlpha = 0.42f
    };
    public DifficultySettings hardSettings = new DifficultySettings
    {
        pieceCount = 22, magnetForce = 2f,  magnetRange = 0.05f, magnetWhileHeld = false,
        spawnMode = SpawnMode.CeilingDrop, startOffset = 0f,
        pieceBrightness = 0.30f, ghostIdleAlpha = 0.05f, ghostActiveAlpha = 0.14f
    };

    // ── References ────────────────────────────────────────────────────────────

    [Header("References")]
    [Tooltip("Pieces are dropped/scattered around this point on a CeilingDrop start")]
    public Transform       puzzleAnchor;
    [Tooltip("Legacy disc radius — superseded by ceilingDropAreaHalf (kept for compatibility).")]
    public float           ceilingDropRadius = 1.6f;
    [Tooltip("Half-extents (m, X×Z) of the rectangle pieces are scattered across on Hard — set to " +
             "roughly the room interior so pieces land EVERYWHERE, not just the centre.")]
    public Vector2         ceilingDropAreaHalf = new Vector2(3.0f, 3.0f);
    [Tooltip("Height (m) pieces drop from on Hard")]
    public float           ceilingDropHeight = 2.6f;
    [Tooltip("Optional — wired by the scene builder; manages MUSE S / behaviour hint colours")]
    public PieceHintSystem hintSystem;

    // ── Runtime state ─────────────────────────────────────────────────────────

    [Header("Runtime State (read-only)")]
    [SerializeField] private DifficultyLevel _currentDifficulty;

    private List<GameObject>  _activePieces    = new();
    private int               _solvedCount;
    private DifficultySettings _baselineSettings;   // the settings chosen at StartPuzzle
    private bool              _puzzleStarted;
    private float            _puzzleStartTime;       // Time.time when the active puzzle began

    /// Wall-clock seconds the most recently completed puzzle took (start → last piece solved).
    /// Read by PuzzleWinHUD to show the solve time. Zero until the first puzzle is completed.
    public float LastPuzzleSeconds { get; private set; }

    /// True while a puzzle is actually running (a difficulty was chosen and pieces are live).
    /// False in the menu / between puzzles / after a solve or restart — used to gate the hint
    /// systems so they never surface cues when the player isn't inside a puzzle.
    public bool IsPuzzleActive => _puzzleStarted;

    /// True while a dark-room difficulty (Medium / Hard) is active — read by PieceHintSystem.
    public bool IsDarkRoom => _puzzleStarted && _currentDifficulty != DifficultyLevel.Easy;

    /// The difficulty of the active puzzle — read by PieceHintSystem to apply the longer Hard-mode
    /// hint timings. Only meaningful while a puzzle is started.
    public DifficultyLevel CurrentDifficulty => _currentDifficulty;

    /// Fired when the player completes a puzzle — DifficultyUI listens to re-open the menu.
    public event Action<DifficultyLevel> OnPuzzleCompleted;

    /// Fired when a puzzle is started — HardModeDarkroom listens to toggle dark/lantern/obstacles.
    public event Action<DifficultyLevel> OnPuzzleStarted;

    /// Fired when the current puzzle is aborted/reset (e.g. the "Restart Puzzle" button) — the
    /// DifficultyUI reopens the menu and HardModeDarkroom returns the room to its lit state.
    public event Action OnPuzzleReset;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        // Hide every piece and snap zone immediately — before the first frame renders.
        HideEverything();
    }

    // ── Difficulty lookups ────────────────────────────────────────────────────

    /// The settings block for a level.
    public DifficultySettings SettingsFor(DifficultyLevel level) => level switch
    {
        DifficultyLevel.Easy   => easySettings,
        DifficultyLevel.Medium => mediumSettings,
        _                      => hardSettings,
    };

    /// The piece set for a level: its own per-difficulty list when populated, otherwise the
    /// first pieceCount of the shared robotPieces list (legacy threshold mode).
    public List<GameObject> PieceSetFor(DifficultyLevel level)
    {
        var perLevel = level switch
        {
            DifficultyLevel.Easy   => easyPieces,
            DifficultyLevel.Medium => mediumPieces,
            _                      => hardPieces,
        };
        if (perLevel != null && perLevel.Count > 0)
            return perLevel;

        int count = Mathf.Clamp(SettingsFor(level).pieceCount, 0, robotPieces.Count);
        return robotPieces.GetRange(0, count);
    }

    /// Number of pieces a level will use — drives the difficulty-selection UI label.
    public int PieceCountFor(DifficultyLevel level) => PieceSetFor(level).Count;

    // ── Public API ────────────────────────────────────────────────────────────

    /// Called by DifficultyUI when the player confirms a difficulty.
    public void StartPuzzle(DifficultyLevel level)
    {
        // Unsubscribe previous listeners before overwriting _activePieces
        foreach (var obj in _activePieces)
        {
            var pp = obj?.GetComponent<PuzzlePiece>();
            if (pp != null) pp.OnPieceSolved -= HandlePieceSolved;
        }

        _currentDifficulty = level;
        _solvedCount       = 0;
        _puzzleStarted     = false;   // reset until baseline is stored below

        HideEverything();

        DifficultySettings s = SettingsFor(level);

        // Whole per-difficulty set, or the first-N legacy slice — copied so we own the list.
        _activePieces = new List<GameObject>(PieceSetFor(level));

        _baselineSettings = s;
        _puzzleStarted    = true;
        _puzzleStartTime  = Time.time;

        var hintPairs = new List<PieceHintSystem.PiecePair>();

        foreach (var pieceObj in _activePieces)
        {
            if (pieceObj == null) continue;
            pieceObj.SetActive(true);

            var piece = pieceObj.GetComponent<PuzzlePiece>();
            if (piece == null) continue;

            // Position the piece for this difficulty (relative to its own solved slot)
            PositionPiece(pieceObj, piece, s);

            piece.isMagneticEnabled = s.magnetForce > 0f;
            piece.magnetForce       = s.magnetForce;
            piece.magnetRange       = s.magnetRange;
            piece.magnetWhileHeld   = s.magnetWhileHeld;
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
                    zone.activationRange = Mathf.Max(s.magnetRange, 0.10f);
                    zone.SetGhostAlpha(s.ghostIdleAlpha, s.ghostActiveAlpha);
                    hintPairs.Add(new PieceHintSystem.PiecePair { piece = piece, snapZone = zone });
                }
            }
        }

        hintSystem?.RegisterPairs(hintPairs);

        OnPuzzleStarted?.Invoke(level);

        Debug.Log($"[PuzzleManager] Robot · {level} — {_activePieces.Count} pieces  " +
                  $"magnet={s.magnetForce:F1}  spawn={s.spawnMode}  brightness={s.pieceBrightness * 100:F0}%");
    }

    /// Aborts the current puzzle and clears the board (the "Restart Puzzle" button). Hides all
    /// pieces/zones, drops listeners, and fires OnPuzzleReset so the DifficultyUI reopens and the
    /// room returns to its lit state.
    public void AbortPuzzle()
    {
        foreach (var obj in _activePieces)
        {
            var pp = obj?.GetComponent<PuzzlePiece>();
            if (pp != null) pp.OnPieceSolved -= HandlePieceSolved;
        }
        _activePieces.Clear();
        _solvedCount   = 0;
        _puzzleStarted = false;

        HideEverything();
        OnPuzzleReset?.Invoke();

        // Put the player back in front of the difficulty panel (the restart used to leave them
        // wherever they had wandered, facing away from the menu).
        var guard = FindFirstObjectByType<PlayerSpawnGuard>();
        if (guard != null) guard.RecenterNow();

        Debug.Log("[PuzzleManager] Puzzle aborted — board cleared.");
    }

    /// Positions a freshly-activated piece based on the difficulty spawn mode.
    private void PositionPiece(GameObject pieceObj, PuzzlePiece piece, DifficultySettings s)
    {
        var rb = pieceObj.GetComponent<Rigidbody>();
        if (rb != null) { rb.isKinematic = false; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }

        Vector3 solved = piece.correctPlacementTarget != null
            ? piece.correctPlacementTarget.position
            : (puzzleAnchor != null ? puzzleAnchor.position : pieceObj.transform.position);

        switch (s.spawnMode)
        {
            case SpawnMode.CeilingDrop:
                Vector3 baseP = puzzleAnchor != null ? puzzleAnchor.position : solved;
                // Uniform over the whole room rectangle (not a centred disc) so Hard pieces land
                // EVERYWHERE — corners included — instead of clustering near the middle.
                float dx = UnityEngine.Random.Range(-ceilingDropAreaHalf.x, ceilingDropAreaHalf.x);
                float dz = UnityEngine.Random.Range(-ceilingDropAreaHalf.y, ceilingDropAreaHalf.y);
                pieceObj.transform.position = new Vector3(baseP.x + dx, ceilingDropHeight, baseP.z + dz);
                pieceObj.transform.rotation = UnityEngine.Random.rotation;
                break;

            case SpawnMode.NearSolved:
            case SpawnMode.OffsetFromSolved:
            default:
                // Scatter on a SHELL between a minimum radius and startOffset — never inside the
                // magnet field. insideUnitSphere used to place some pieces within magnetRange of
                // their slot, so they auto-snapped on spawn ("autocomplete at the start"). The min
                // radius is kept comfortably above magnetRange so every piece starts clearly out of
                // range and must be carried in by the player.
                float minR = Mathf.Max(s.magnetRange * 1.6f, s.startOffset * 0.5f);
                float maxR = Mathf.Max(minR, s.startOffset);
                Vector3 dir = UnityEngine.Random.onUnitSphere;
                dir.y = Mathf.Abs(dir.y) * 0.5f;   // bias upward so pieces don't spawn under the table
                if (dir.sqrMagnitude < 1e-4f) dir = Vector3.up;
                pieceObj.transform.position = solved + dir.normalized * UnityEngine.Random.Range(minR, maxR);
                break;
        }
    }

    // ── Adaptive assistance (called by AdaptiveDifficultyController) ─────────

    /// Interpolates active piece and snap-zone settings between the player's chosen baseline
    /// (blend=0) and maximum Easy-mode assistance (blend=1). Called every frame while stress
    /// is elevated.
    public void OverrideAssistance(float blend)
    {
        if (!_puzzleStarted) return;

        var s = new DifficultySettings
        {
            magnetForce      = Mathf.Lerp(_baselineSettings.magnetForce,      easySettings.magnetForce,      blend),
            magnetRange      = Mathf.Lerp(_baselineSettings.magnetRange,      easySettings.magnetRange,      blend),
            pieceBrightness  = Mathf.Lerp(_baselineSettings.pieceBrightness,  easySettings.pieceBrightness,  blend),
            ghostIdleAlpha   = Mathf.Lerp(_baselineSettings.ghostIdleAlpha,   easySettings.ghostIdleAlpha,   blend),
            ghostActiveAlpha = Mathf.Lerp(_baselineSettings.ghostActiveAlpha, easySettings.ghostActiveAlpha, blend),
        };

        foreach (var pieceObj in _activePieces)
        {
            if (pieceObj == null) continue;
            var piece = pieceObj.GetComponent<PuzzlePiece>();
            if (piece == null || piece.IsSolved) continue;

            piece.isMagneticEnabled = s.magnetForce > 0f;
            piece.magnetForce       = s.magnetForce;
            piece.magnetRange       = s.magnetRange;
            piece.SetVisibility(s.pieceBrightness);

            if (piece.correctPlacementTarget != null)
            {
                var zone = piece.correctPlacementTarget.GetComponent<MagneticSnapZone>();
                if (zone != null)
                {
                    zone.activationRange = Mathf.Max(s.magnetRange, 0.10f);
                    zone.SetGhostAlpha(s.ghostIdleAlpha, s.ghostActiveAlpha);
                }
            }
        }
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
        LastPuzzleSeconds = Time.time - _puzzleStartTime;
        Debug.Log($"[PuzzleManager] Puzzle complete! (Robot · {_currentDifficulty}) in {LastPuzzleSeconds:F1}s");

        // Mark the solve on the PC session log (difficulty + elapsed seconds) so the plot can shade
        // the gameplay span and label its duration (no-op on the BLE build, no UDP bridge).
        MuseUdpAdapter.Instance?.SendCommand($"solved:{_currentDifficulty}:{LastPuzzleSeconds:F1}");

        // Clear the finished board BEFORE notifying listeners so the menu reopens onto an empty
        // room — the solved robot used to stay sitting there ("the puzzle is still behind"), and on
        // dark difficulties the room stayed pitch black until a manual restart.
        _puzzleStarted = false;
        HideEverything();

        // PuzzleWinHUD reads LastPuzzleSeconds; DifficultyUI reopens the menu; HardModeDarkroom
        // restores the room lighting — all via this event.
        OnPuzzleCompleted?.Invoke(_currentDifficulty);

        // Face the player back at the difficulty panel for the next pick.
        var guard = FindFirstObjectByType<PlayerSpawnGuard>();
        if (guard != null) guard.RecenterNow();
    }

    /// Every managed piece across the shared list and all per-difficulty sets.
    private IEnumerable<GameObject> AllPieces()
    {
        foreach (var p in robotPieces)  yield return p;
        foreach (var p in easyPieces)   yield return p;
        foreach (var p in mediumPieces) yield return p;
        foreach (var p in hardPieces)   yield return p;
    }

    /// Deactivate every piece and its snap zone (any difficulty).
    private void HideEverything()
    {
        foreach (var pieceObj in AllPieces())
        {
            if (pieceObj == null) continue;
            pieceObj.SetActive(false);
            var piece = pieceObj.GetComponent<PuzzlePiece>();
            if (piece?.correctPlacementTarget != null)
                piece.correctPlacementTarget.gameObject.SetActive(false);
        }
    }
}
