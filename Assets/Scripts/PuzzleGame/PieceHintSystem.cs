using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// Manages colour-matching hints across all active piece–snap-zone pairs.
///
/// When CognitiveLoadAdapter reports stress above its threshold (indicating the
/// player is cognitively overloaded), each puzzle piece and its matching snap zone
/// smoothly fade to the same distinct hint colour so the player can identify
/// which piece belongs where without having to think about shape alone.
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
    }

    [Tooltip("Time in seconds to fully fade hints in or out")]
    public float transitionDuration = 0.8f;

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
    private bool             _hintsActive;
    private Coroutine        _fadeRoutine;

    private void Start()
    {
        if (CognitiveLoadAdapter.Instance != null)
            CognitiveLoadAdapter.Instance.OnStressChanged += HandleStressChanged;
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
            _activePairs[i].hintColor = k_Palette[i % k_Palette.Length];

        // Snap immediately to current state (no fade on first registration)
        float blend = _hintsActive ? 1f : 0f;
        foreach (var pair in _activePairs)
        {
            pair.piece?.SetHintColor(pair.hintColor, blend);
            pair.snapZone?.SetHintColor(pair.hintColor, blend);
        }
    }

    private void HandleStressChanged(float _)
    {
        bool want = CognitiveLoadAdapter.Instance.HintsActive;
        if (want == _hintsActive) return;
        _hintsActive = want;

        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(FadeHints(_hintsActive ? 1f : 0f));
    }

    private IEnumerator FadeHints(float targetBlend)
    {
        float startBlend = 1f - targetBlend;
        float elapsed    = 0f;

        while (elapsed < transitionDuration)
        {
            elapsed += Time.deltaTime;
            float blend = Mathf.Lerp(startBlend, targetBlend, elapsed / transitionDuration);
            foreach (var pair in _activePairs)
            {
                if (pair.piece != null && !pair.piece.IsSolved)
                    pair.piece.SetHintColor(pair.hintColor, blend);
                if (pair.snapZone != null && !pair.snapZone.IsSolved)
                    pair.snapZone.SetHintColor(pair.hintColor, blend);
            }
            yield return null;
        }
    }
}
