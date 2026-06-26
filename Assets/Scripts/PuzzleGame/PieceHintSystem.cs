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
///   • blink        — ≥ blinkIdleSeconds have passed since the last piece was placed AND their hands
///                    are empty (they're stuck and not making progress, not mid-placement).
///   Behaviour vs Muse are SEPARATE: at the full timers above a cue is a Behaviour hint (needs the
///   Behaviour helper). When sustained Muse stress is high (and the Muse helper is on) the same
///   triggers fire EARLY — at half the time — and a cue inside that early window is credited to Muse
///   instead. So a stressed player gets help sooner, behaviour cues are never mislabelled as Muse,
///   and Muse cues can still appear with the Behaviour helper off. It still only surfaces ONE piece.
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

    [Header("Hard-mode hint timing (longer — holding a piece means you're not lost)")]
    [Tooltip("Hard mode: accumulated HELD seconds on one piece before its colour-match hint appears " +
             "while that piece is still AWAY from the robot. Longer than the base time because " +
             "holding a piece means the player isn't lost — they're carrying it somewhere.")]
    public float hardColorMatchHoldSeconds = 40f;
    [Tooltip("Hard mode: accumulated HELD seconds before the colour-match hint when the held piece " +
             "is already NEAR the robot — even longer, because they're clearly lining it up to place.")]
    public float hardColorMatchHoldNearRobotSeconds = 60f;
    [Tooltip("Hard mode: seconds with NO piece placed AND NO piece currently in hand before the " +
             "blink hint fires — the empty-handed, stalled, 'might actually be lost' timer.")]
    public float hardBlinkIdleSeconds = 60f;
    [Tooltip("Distance (m) from the robot/puzzle anchor within which a held piece counts as 'near " +
             "the robot' for the longer Hard colour-match timer.")]
    public float nearRobotRange = 0.8f;

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

    /// True when a piece is within nearRobotRange of the robot/puzzle anchor — used to extend the
    /// Hard colour-match timer for a held piece that's already being lined up to place.
    private bool IsNearRobot(PuzzlePiece piece)
    {
        if (piece == null || puzzleManager == null || puzzleManager.puzzleAnchor == null) return false;
        return (piece.transform.position - puzzleManager.puzzleAnchor.position).sqrMagnitude
               <= nearRobotRange * nearRobotRange;
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

    /// Picks the single piece to hint next, honestly attributing the cue to its cause:
    ///   • Behaviour hint — the player held one piece too long, or stalled empty-handed for too long.
    ///                      Fires at the FULL timers, only while the Behaviour helper is enabled.
    ///   • Muse hint      — sustained Muse stress is high AND the Muse helper is enabled: the SAME
    ///                      triggers fire EARLY (half the time), and a cue that fires inside that
    ///                      accelerated window is credited to Muse, not behaviour.
    /// So the two helpers are independent: behaviour cues never get tagged Muse just because the
    /// player happens to be stressed, and Muse cues can appear even with the Behaviour helper off.
    /// Returns the chosen pair (already announced on the event bus), or null if nothing should fire.
    private PiecePair ChooseFocus(List<PiecePair> ordered)
    {
        bool behavior = AssistanceSettings.BehaviorHelperEnabled;
        bool muse     = _globalHintWant && AssistanceSettings.MuseHelperEnabled;
        bool hard     = puzzleManager != null && puzzleManager.CurrentDifficulty == DifficultyLevel.Hard;

        // Most-held unsolved piece (colour-match candidate) + whether ANY piece is in hand right now.
        PiecePair held       = null;
        bool      anyHeldNow = false;
        foreach (var p in ordered)
        {
            if (p.piece.IsHeld) anyHeldNow = true;
            if ((p.piece.IsHeld || p.piece.TotalHeldSeconds > 0f) &&
                (held == null || p.piece.TotalHeldSeconds > held.piece.TotalHeldSeconds))
                held = p;
        }

        // Colour-match: they keep holding one piece but can't place it. Hard waits longer (a held
        // piece means they're not lost), longer still when it's already by the robot.
        float colorBase = hard ? hardColorMatchHoldSeconds : colorMatchHoldSeconds;
        if (hard && held != null && held.piece.IsHeld && IsNearRobot(held.piece))
            colorBase = hardColorMatchHoldNearRobotSeconds;
        float heldSecs = held != null ? held.piece.TotalHeldSeconds : 0f;

        // Blink: stalled with EMPTY hands. Holding a piece (any difficulty) means they're working it,
        // not lost — so the blink never fires while something is in hand.
        float blinkBase = hard ? hardBlinkIdleSeconds : blinkIdleSeconds;
        float idleSecs  = Time.time - _lastPlaceTime;
        bool  emptyHand = !anyHeldNow;

        // Muse fires the same triggers at HALF the time; a cue inside that early window is a Muse cue.
        bool colorMuse = muse     && held != null && heldSecs >= Mathf.Max(4f, colorBase * 0.5f);
        bool colorBeh  = behavior && held != null && heldSecs >= Mathf.Max(4f, colorBase);
        bool blinkMuse = muse     && emptyHand && idleSecs >= Mathf.Max(4f, blinkBase * 0.5f);
        bool blinkBeh  = behavior && emptyHand && idleSecs >= Mathf.Max(4f, blinkBase);

        if (colorMuse || colorBeh)
        {
            AdaptiveEventBus.Report($"Hint: '{Pretty(held.piece.name)}' matches its coloured slot",
                                    colorMuse ? AdaptiveSignal.MuseStress : AdaptiveSignal.Behavior);
            return held;
        }
        if (blinkMuse || blinkBeh)
        {
            var pick = ordered[0];   // nearest unsolved
            AdaptiveEventBus.Report($"Hint: look for the glowing '{Pretty(pick.piece.name)}'",
                                    blinkMuse ? AdaptiveSignal.MuseStress : AdaptiveSignal.Behavior);
            return pick;
        }
        return null;
    }

    /// Fades every pair's hint colour/blink to zero (used when the behaviour helper is disabled).
    private void FadeAllOut(float step)
    {
        foreach (var pair in _activePairs)
        {
            if (pair.piece == null) continue;
            pair.blend = Mathf.MoveTowards(pair.blend, 0f, step);
            if (!pair.piece.IsSolved)
            {
                pair.piece.SetHintColor(pair.hintColor, pair.blend);
                pair.snapZone?.SetHintColor(pair.hintColor, pair.blend);
            }
            if (pair.glowing) { pair.piece.SetGlow(false); pair.glowing = false; }
        }
    }

    private void Update()
    {
        if (_activePairs.Count == 0) return;

        float step = transitionDuration > 0f ? Time.deltaTime / transitionDuration : 1f;

        // No puzzle running (menu / between puzzles / after a solve or a "Restart Puzzle") → never
        // surface a hint. A finished or aborted board still holds its pairs, so without this gate the
        // blink/colour timers keep accruing and a cue can fire while the player is back at the menu.
        if (puzzleManager != null && !puzzleManager.IsPuzzleActive)
        {
            _focus         = null;
            _lastPlaceTime = Time.time;   // keep the blink timer from accruing off-puzzle
            FadeAllOut(step);
            return;
        }

        // Both helpers switched off in the session menu → never surface a hint. (Either one on is
        // enough to run: behaviour hints need BehaviorHelper, Muse-accelerated hints need MuseHelper.)
        if (!AssistanceSettings.BehaviorHelperEnabled && !AssistanceSettings.MuseHelperEnabled)
        {
            _focus         = null;
            _lastPlaceTime = Time.time;   // don't let the blink timer accrue while disabled
            FadeAllOut(step);
            return;
        }

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
            _focus = ChooseFocus(ordered);

        ApplyFocus(step);
    }

    /// Drives every pair toward its target: ONLY the focus piece fades up to its colour + blink,
    /// everything else fades back to zero.
    private void ApplyFocus(float step)
    {
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
            if (isFocus != pair.glowing)
            {
                pair.piece.SetGlow(isFocus);
                pair.glowing = isFocus;
            }
        }
    }
}
