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
    [Tooltip("Time in seconds to fully fade hints in or out")]
    public float transitionDuration = 0.8f;

    [Header("Per-piece struggle triggers")]
    [Tooltip("Seconds a single piece may stay unsolved before its colour hint appears")]
    public float struggleSeconds = 22f;
    [Tooltip("Number of grabs of one piece before its colour hint appears")]
    public int   struggleHeldCount = 4;
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
    private bool             _prevGlobalHintWant;

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

    private void Update()
    {
        if (_activePairs.Count == 0) return;

        bool dark   = puzzleManager != null && puzzleManager.IsDarkRoom;
        float step  = transitionDuration > 0f ? Time.deltaTime / transitionDuration : 1f;

        // Global colour-hint switched on by Muse stress → report once (affects every piece).
        if (_globalHintWant && !_prevGlobalHintWant)
            AdaptiveEventBus.Report("Colour hints on — each piece matched to its slot", AdaptiveSignal.MuseStress);
        _prevGlobalHintWant = _globalHintWant;

        foreach (var pair in _activePairs)
        {
            var piece = pair.piece;
            if (piece == null || piece.IsSolved)
            {
                if (pair.glowing) { pair.piece?.SetGlow(false); pair.glowing = false; }
                continue;
            }

            // Colour hint: global overload OR this specific piece has been a struggle.
            bool struggling = piece.UnsolvedSeconds >= struggleSeconds ||
                              piece.HeldCount      >= struggleHeldCount;

            // Per-piece struggle (behaviour) hint — report once, only when global isn't already on.
            if (struggling && !_globalHintWant && !pair.struggleReported)
            {
                AdaptiveEventBus.Report($"Colour hint: '{Pretty(piece.name)}' (stuck on it)", AdaptiveSignal.Behavior);
                pair.struggleReported = true;
            }

            float target = (_globalHintWant || struggling) ? 1f : 0f;

            pair.blend = Mathf.MoveTowards(pair.blend, target, step);
            piece.SetHintColor(pair.hintColor, pair.blend);
            pair.snapZone?.SetHintColor(pair.hintColor, pair.blend);

            // Find-me flicker for a piece lost in the dark.
            bool wantGlow = dark && piece.UnsolvedSeconds >= lostSeconds;
            if (wantGlow != pair.glowing)
            {
                piece.SetGlow(wantGlow);
                pair.glowing = wantGlow;
                if (wantGlow)
                    AdaptiveEventBus.Report($"Find-me glow: '{Pretty(piece.name)}' (lost in the dark)", AdaptiveSignal.Behavior);
            }
        }
    }
}
