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
/// WHEN A HINT APPEARS (one focus piece, chosen the moment a trigger fires):
///
///   BEHAVIOUR triggers (need the Behaviour helper; time-based):
///     • colour-match — accumulated HOLD time on one piece: Easy/Med 15 s, Hard 20 s. On HARD it only
///                      appears while that held piece is NEAR the robot (within nearRobotRange) — i.e.
///                      they're clearly lining it up, not still carrying it. On Easy/Medium it appears
///                      wherever the piece is held.
///     • blink        — ≥ blinkIdleSeconds (Easy/Med 20 s, Hard 30 s) since the last placement with
///                      EMPTY hands (stalled, not lining anything up). Resets on every placement.
///
///   MUSE / EEG trigger (needs the Muse helper; stress-based, INSTANT — bypasses the timers above):
///     When sustained stress is high (StressLevel ≥ CognitiveLoadAdapter.hintThreshold) the system
///     relieves the overload immediately, routed by where the player is working:
///       • a piece being lined up at the robot → instant colour-match on that piece.
///       • not at the robot (empty hands)       → instant blink on the nearest unsolved piece.
///     The Muse path is independent of the Behaviour helper (it can fire with Behaviour hints off),
///     and a cue it raises is credited to Muse; the slower behaviour timers are credited to Behaviour.
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
    [Tooltip("Easy/Medium: accumulated seconds the player may HOLD a single piece before its " +
             "colour-match hint (piece + slot share a colour) appears.")]
    public float colorMatchHoldSeconds = 15f;
    [Tooltip("Hard: accumulated HELD seconds before the colour-match hint — and only while that held " +
             "piece is near the robot (see nearRobotRange). Longer than Easy/Medium.")]
    public float hardColorMatchHoldSeconds = 20f;
    [Tooltip("Easy/Medium: seconds since the LAST piece was placed (with empty hands) before the " +
             "next unsolved piece starts to blink. Resets every time a piece is placed.")]
    public float blinkIdleSeconds = 20f;
    [Tooltip("Hard mode: longer empty-handed idle time before the blink hint fires.")]
    public float hardBlinkIdleSeconds = 30f;
    [Tooltip("Distance (m) from the robot/puzzle anchor within which a HELD piece counts as 'being " +
             "lined up'. On Hard the colour-match hint only fires inside this range; the Muse stress " +
             "path also uses it to route near→colour-match vs far→blink.")]
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

    /// Picks the single piece to hint next, attributing the cue to its cause:
    ///   • Muse cue (instant) — stress is high (≥ hintThreshold) AND the Muse helper is on. It skips
    ///                          the behaviour timers and routes by position: a piece being lined up at
    ///                          the robot → colour-match it; otherwise (empty hands) → blink the nearest.
    ///   • Behaviour cue (timed) — needs the Behaviour helper: colour-match after colorMatchHoldSeconds
    ///                          of hold (on Hard only while a held piece is near the robot), or blink
    ///                          after blinkIdleSeconds idle with empty hands.
    /// The two helpers are independent: Muse cues can appear with the Behaviour helper off, and
    /// behaviour cues fire on their timers regardless of stress.
    /// Returns the chosen pair (already announced on the event bus), or null if nothing should fire.
    private PiecePair ChooseFocus(List<PiecePair> ordered)
    {
        bool behavior = AssistanceSettings.BehaviorHelperEnabled;
        bool muse     = _globalHintWant && AssistanceSettings.MuseHelperEnabled;
        bool hard     = puzzleManager != null && puzzleManager.CurrentDifficulty == DifficultyLevel.Hard;

        // One scan of the unsolved pieces:
        //   • held       — the piece held the LONGEST in total (colour-match candidate on Easy/Medium).
        //   • liningUp    — a piece held RIGHT NOW that is near the robot: the "lining a piece up to
        //                   place it" state (colour-match candidate on Hard, and the Muse near route).
        //   • anyHeldNow  — is anything in hand at all (blink only fires with empty hands).
        PiecePair held = null, liningUp = null;
        bool      anyHeldNow = false;
        foreach (var p in ordered)
        {
            if (p.piece.IsHeld)
            {
                anyHeldNow = true;
                if (liningUp == null && IsNearRobot(p.piece)) liningUp = p;
            }
            if ((p.piece.IsHeld || p.piece.TotalHeldSeconds > 0f) &&
                (held == null || p.piece.TotalHeldSeconds > held.piece.TotalHeldSeconds))
                held = p;
        }
        bool emptyHand = !anyHeldNow;

        // ── Muse / EEG: sustained stress is high (≥ hintThreshold) and the Muse helper is on. Skip the
        //    behaviour timers and give INSTANT help, routed by where the player is working:
        //      • lining a piece up at the robot → instant colour-match on that piece (ease the place).
        //      • not at the robot (empty hands)  → instant blink on the nearest piece (guide them in).
        //    The point of the Muse path is to relieve the overload the moment it's detected, rather
        //    than waiting out a stall timer — so neither branch has a time threshold of its own.
        if (muse && liningUp != null)
        {
            AdaptiveEventBus.Report($"Hint: '{Pretty(liningUp.piece.name)}' matches its coloured slot",
                                    AdaptiveSignal.MuseStress);
            return liningUp;
        }
        if (muse && emptyHand)
        {
            var pick = ordered[0];
            AdaptiveEventBus.Report($"Hint: look for the glowing '{Pretty(pick.piece.name)}'",
                                    AdaptiveSignal.MuseStress);
            return pick;
        }

        // ── Behaviour: time-based, needs the Behaviour helper. ──
        // Colour-match: they keep holding one piece but can't place it (≥ colorMatchHoldSeconds of
        // total hold). On Hard it ONLY fires while a held piece is near the robot (liningUp) — a piece
        // held out in the room means they're still carrying it, not stuck lining it up.
        PiecePair colorCand = hard ? liningUp : held;
        float     colorBase = hard ? hardColorMatchHoldSeconds : colorMatchHoldSeconds;
        if (behavior && colorCand != null && colorCand.piece.TotalHeldSeconds >= colorBase)
        {
            AdaptiveEventBus.Report($"Hint: '{Pretty(colorCand.piece.name)}' matches its coloured slot",
                                    AdaptiveSignal.Behavior);
            return colorCand;
        }

        // Blink: stalled with EMPTY hands for too long (not lining anything up). Resets on placement.
        float blinkBase = hard ? hardBlinkIdleSeconds : blinkIdleSeconds;
        float idleSecs  = Time.time - _lastPlaceTime;
        if (behavior && emptyHand && idleSecs >= blinkBase)
        {
            var pick = ordered[0];   // nearest unsolved
            AdaptiveEventBus.Report($"Hint: look for the glowing '{Pretty(pick.piece.name)}'",
                                    AdaptiveSignal.Behavior);
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
