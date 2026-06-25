using System;
using System.Collections.Generic;
using UnityEngine;

/// Manages colour-matching hints across all active piece–snap-zone pairs, plus a "find me"
/// flicker for pieces lost in a dark room.
///
/// A piece + its snap zone fade to the same distinct palette colour when EITHER:
///   • global overload  — CognitiveLoadAdapter reports stress above its hint threshold, OR
///   • per-piece struggle — the player has spent too long on, or repeatedly grabbed, THAT piece.
///
/// In a dark room, a piece that stays unsolved for a long time also flickers (emissive glow)
/// so the player can find it, then match it to its same-coloured ghost.
///
/// PuzzleManager calls RegisterPairs() each time a puzzle starts.
public class PieceHintSystem : MonoBehaviour
{
    [Serializable]
    public class PiecePair
    {
        public PuzzlePiece      piece;
        public MagneticSnapZone snapZone;
        [HideInInspector] public Color hintColor;
        [HideInInspector] public float blend;   // current 0–1 hint blend for this pair
        [HideInInspector] public bool  glowing;
        [HideInInspector] public bool  struggleReported;   // behaviour colour-hint event fired once
    }

    [Header("Fade")]
    [Tooltip("Time in seconds to fully fade a hint in or out (higher = slower, gentler).")]
    public float transitionDuration = 1.3f;
    [Tooltip("Seconds between successive help reveals. Help escalates ONE piece at a time at this " +
             "cadence while the player stays stuck/stressed (nearest piece first), and retreats at " +
             "the same rate once they recover — so hints never all appear at once.")]
    public float hintInterval = 6f;

    [Header("Per-piece struggle triggers")]
    [Tooltip("Seconds a single piece may stay unsolved before its colour hint appears")]
    public float struggleSeconds = 32f;
    [Tooltip("Number of grabs of one piece before its colour hint appears")]
    public int   struggleHeldCount = 6;
    [Tooltip("Seconds a piece may stay lost in a DARK room before it starts flickering")]
    public float lostSeconds = 30f;

    [Header("References")]
    [Tooltip("Optional — wired by the scene builder; used to know whether the room is dark.")]
    public PuzzleManager puzzleManager;

    // 12 perceptually distinct colours drawn from a calm, readable palette
    private static readonly Color[] k_Palette =
    {
        new Color(0.95f, 0.28f, 0.28f),   //  0 red
        new Color(0.28f, 0.52f, 1.00f),   //  1 blue
        new Color(0.22f, 0.88f, 0.38f),   //  2 green
        new Color(1.00f, 0.80f, 0.10f),   //  3 yellow
        new Color(0.82f, 0.24f, 0.90f),   //  4 purple
        new Color(1.00f, 0.55f, 0.12f),   //  5 orange
        new Color(0.12f, 0.82f, 0.76f),   //  6 teal
        new Color(1.00f, 0.42f, 0.70f),   //  7 pink
        new Color(0.42f, 0.80f, 0.22f),   //  8 lime
        new Color(0.60f, 0.82f, 1.00f),   //  9 sky
        new Color(0.90f, 0.60f, 0.20f),   // 10 amber
        new Color(0.20f, 0.80f, 0.95f),   // 11 cyan
    };

    private List<PiecePair> _activePairs = new();
    private bool             _globalHintWant;
    private int              _helpLevel;       // how many pieces are currently being helped
    private float            _nextStepTime;    // when help may next escalate / retreat

    private void Start()
    {
        if (CognitiveLoadAdapter.Instance != null)
        {
            CognitiveLoadAdapter.Instance.OnStressChanged += HandleStressChanged;
            _globalHintWant = CognitiveLoadAdapter.Instance.HintsActive;
        }
    }

    private void OnDestroy()
    {
        if (CognitiveLoadAdapter.Instance != null)
            CognitiveLoadAdapter.Instance.OnStressChanged -= HandleStressChanged;
    }

    /// Called by PuzzleManager whenever a new puzzle starts.
    /// Assigns palette colours to pairs and snaps to the current hint state.
    public void RegisterPairs(List<PiecePair> pairs)
    {
        _activePairs = pairs;
        for (int i = 0; i < _activePairs.Count; i++)
        {
            var pair = _activePairs[i];
            pair.hintColor       = k_Palette[i % k_Palette.Length];
            pair.blend           = _globalHintWant ? 1f : 0f;
            pair.glowing         = false;
            pair.struggleReported = false;
            pair.piece?.SetHintColor(pair.hintColor, pair.blend);
            pair.snapZone?.SetHintColor(pair.hintColor, pair.blend);
            pair.piece?.SetGlow(false);
        }
    }

    private void HandleStressChanged(float _)
    {
        if (CognitiveLoadAdapter.Instance != null)
            _globalHintWant = CognitiveLoadAdapter.Instance.HintsActive;
    }

    /// Trims the "Piece_Robot_" prefix for readable event text (e.g. "Head", "LeftArm").
    private static string Pretty(string n)
    {
        int i = n.LastIndexOf('_');
        return i >= 0 && i < n.Length - 1 ? n.Substring(i + 1) : n;
    }

    /// Unsolved active pairs ordered nearest-to-the-player first (the order help is granted in).
    private List<PiecePair> OrderedUnsolved()
    {
        var cam = Camera.main;
        Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
        var list = new List<PiecePair>();
        foreach (var p in _activePairs)
            if (p.piece != null && !p.piece.IsSolved) list.Add(p);
        list.Sort((a, b) =>
            (a.piece.transform.position - eye).sqrMagnitude
            .CompareTo((b.piece.transform.position - eye).sqrMagnitude));
        return list;
    }

    private void Update()
    {
        if (_activePairs.Count == 0) return;

        bool  dark = puzzleManager != null && puzzleManager.IsDarkRoom;
        float step = transitionDuration > 0f ? Time.deltaTime / transitionDuration : 1f;

        var ordered = OrderedUnsolved();
        _helpLevel  = Mathf.Clamp(_helpLevel, 0, ordered.Count);

        // ── Is help WANTED right now? ───────────────────────────────────────────
        // Muse: sustained stress above the (baseline-relative) hint threshold.
        bool museWant = _globalHintWant;
        // Behaviour: the player has been stuck on the puzzle for a while, or re-grabbed a lot.
        bool stuckWant = false;
        foreach (var p in ordered)
            if (p.piece.UnsolvedSeconds >= struggleSeconds || p.piece.HeldCount >= struggleHeldCount)
            { stuckWant = true; break; }
        bool wanted = museWant || stuckWant;

        // ── Escalate / retreat help ONE piece at a time, at hintInterval cadence ──
        if (Time.time >= _nextStepTime)
        {
            int prev = _helpLevel;
            if      (wanted && _helpLevel < ordered.Count) _helpLevel++;
            else if (!wanted && _helpLevel > 0)            _helpLevel--;

            if (_helpLevel != prev)
            {
                _nextStepTime = Time.time + Mathf.Max(0.5f, hintInterval);
                if (_helpLevel > prev)   // a new piece just gained help → announce it
                {
                    var np  = ordered[_helpLevel - 1];
                    var sig = museWant ? AdaptiveSignal.MuseStress : AdaptiveSignal.Behavior;
                    AdaptiveEventBus.Report($"Hint: '{Pretty(np.piece.name)}' — matched to its slot", sig);
                }
            }
        }

        // ── Apply: the first _helpLevel nearest-unsolved pieces show the colour hint ──
        for (int i = 0; i < ordered.Count; i++)
        {
            var pair  = ordered[i];
            bool help = i < _helpLevel;
            pair.blend = Mathf.MoveTowards(pair.blend, help ? 1f : 0f, step);
            pair.piece.SetHintColor(pair.hintColor, pair.blend);
            pair.snapZone?.SetHintColor(pair.hintColor, pair.blend);

            // Find-me glow: ONLY the single focus piece (the nearest helped one) flickers in the
            // dark — pieces never all flicker at once; it points to the one to place next.
            bool wantGlow = dark && help && i == 0 && pair.piece.UnsolvedSeconds >= lostSeconds;
            if (wantGlow != pair.glowing)
            {
                pair.piece.SetGlow(wantGlow);
                pair.glowing = wantGlow;
            }
        }

        // Clear any lingering glow on pieces that have since been solved.
        foreach (var pair in _activePairs)
            if (pair.piece != null && pair.piece.IsSolved && pair.glowing)
            { pair.piece.SetGlow(false); pair.glowing = false; }
    }
}
