using System;
using System.Collections.Generic;
using UnityEngine;

/// Drives the colour-match + "find-me" blink hints for the robot puzzle.
///
/// DESIGN (one hint, one piece, sequential):
///   The system ever highlights AT MOST ONE piece at a time — the current "focus" piece. It and
///   its matching snap zone fade to the same distinct palette colour, and the piece flickers
///   (emissive blink) so it is unmistakable which piece to place next. The focus stays LOCKED on
///   that one piece until the player actually places it; only then can the next hint appear. So
///   hints never flood the board, and a new colour/blink cue only shows up after the hinted piece
///   has been solved.
///
/// WHEN A HINT APPEARS (the focus is chosen when EITHER trigger fires):
///   • colour-match — the player has accumulated ≥ colorMatchHoldSeconds of HOLD time on one piece
///                    (they keep picking it up but can't find where it goes).
///   • blink        — ≥ blinkIdleSeconds have passed since the last piece was placed (they're stuck
///                    and not making progress).
///   A high sustained Muse stress reading halves both wait times so an overwhelmed player gets help
///   sooner, but it still only ever surfaces ONE piece.
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
    }

    [Header("Fade")]
    [Tooltip("Time in seconds to fully fade a hint in or out (higher = slower, gentler).")]
    public float transitionDuration = 1.0f;

    [Header("Hint triggers (one piece at a time)")]
    [Tooltip("Accumulated seconds the player may HOLD a single piece before its colour-match hint " +
             "(piece + slot share a colour) appears. They keep grabbing it but can't place it.")]
    public float colorMatchHoldSeconds = 20f;
    [Tooltip("Seconds since the LAST piece was placed before the next unsolved piece starts to " +
             "blink. Resets every time a piece is placed, so the next hint only comes after real " +
             "progress stalls again.")]
    public float blinkIdleSeconds = 20f;

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
    private bool             _globalHintWant;   // sustained Muse stress above the hint threshold
    private PiecePair        _focus;            // the single piece currently being hinted (locked)
    private float            _lastPlaceTime;    // Time.time a piece was last placed (blink timer base)
    private int              _prevSolvedCount;

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
    /// Assigns palette colours to pairs and clears any active hint.
    public void RegisterPairs(List<PiecePair> pairs)
    {
        _activePairs = pairs;
        _focus           = null;
        _prevSolvedCount = 0;
        _lastPlaceTime   = Time.time;   // first blink only after blinkIdleSeconds of no progress
        for (int i = 0; i < _activePairs.Count; i++)
        {
            var pair = _activePairs[i];
            pair.hintColor = k_Palette[i % k_Palette.Length];
            pair.blend     = 0f;
            pair.glowing   = false;
            pair.piece?.SetHintColor(pair.hintColor, 0f);
            pair.snapZone?.SetHintColor(pair.hintColor, 0f);
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

    /// Unsolved active pairs ordered nearest-to-the-player first.
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

        float step = transitionDuration > 0f ? Time.deltaTime / transitionDuration : 1f;

        var ordered = OrderedUnsolved();

        // ── Track placement progress → reset the blink timer whenever a piece is placed ──
        int solved = _activePairs.Count - ordered.Count;
        if (solved > _prevSolvedCount)
        {
            _prevSolvedCount = solved;
            _lastPlaceTime   = Time.time;   // next blink waits a fresh blinkIdleSeconds
        }

        // ── Release the focus once its piece is placed (lets the NEXT hint appear) ──
        if (_focus != null && (_focus.piece == null || _focus.piece.IsSolved))
            _focus = null;

        // ── Choose a focus if none is locked and a trigger has fired ──
        if (_focus == null && ordered.Count > 0)
        {
            float mul       = _globalHintWant ? 0.5f : 1f;   // Muse stress → help sooner
            float colorNeed = Mathf.Max(4f, colorMatchHoldSeconds * mul);
            float blinkNeed = Mathf.Max(4f, blinkIdleSeconds     * mul);

            // colour-match: the piece they keep holding but can't place.
            PiecePair held = null;
            foreach (var p in ordered)
                if ((p.piece.IsHeld || p.piece.TotalHeldSeconds > 0f) &&
                    (held == null || p.piece.TotalHeldSeconds > held.piece.TotalHeldSeconds))
                    held = p;
            bool colorTrig = held != null && held.piece.TotalHeldSeconds >= colorNeed;

            // blink: stalled — no piece placed for a while.
            bool blinkTrig = (Time.time - _lastPlaceTime) >= blinkNeed;

            if (colorTrig)
            {
                _focus = held;
                AdaptiveEventBus.Report($"Hint: '{Pretty(_focus.piece.name)}' matches its coloured slot",
                                        _globalHintWant ? AdaptiveSignal.MuseStress : AdaptiveSignal.Behavior);
            }
            else if (blinkTrig)
            {
                _focus = ordered[0];   // nearest unsolved
                AdaptiveEventBus.Report($"Hint: look for the glowing '{Pretty(_focus.piece.name)}'",
                                        _globalHintWant ? AdaptiveSignal.MuseStress : AdaptiveSignal.Behavior);
            }
        }

        // ── Apply: ONLY the focus piece shows the colour + blink; everything else fades out ──
        foreach (var pair in _activePairs)
        {
            if (pair.piece == null) continue;
            bool isFocus = pair == _focus && !pair.piece.IsSolved;

            pair.blend = Mathf.MoveTowards(pair.blend, isFocus ? 1f : 0f, step);
            if (!pair.piece.IsSolved)
            {
                pair.piece.SetHintColor(pair.hintColor, pair.blend);
                pair.snapZone?.SetHintColor(pair.hintColor, pair.blend);
            }

            // Blink (emissive flicker) only on the single focus piece.
            bool wantGlow = isFocus;
            if (wantGlow != pair.glowing)
            {
                pair.piece.SetGlow(wantGlow);
                pair.glowing = wantGlow;
            }
        }
    }
}
