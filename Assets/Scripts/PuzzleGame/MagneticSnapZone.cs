using UnityEngine;

/// Attach to each snap-target Transform — the same GameObject assigned as
/// "correctPlacementTarget" on a PuzzlePiece.
///
/// Ghost visibility by difficulty:
///   PuzzleManager calls SetGhostAlpha(idle, active) after each StartPuzzle.
///   Easy  → idle 0.45 / active 0.70  (clearly visible silhouette)
///   Medium → idle 0.20 / active 0.40  (subtle hint)
///   Hard   → idle 0.04 / active 0.12  (barely visible — you have to find the target)
///
/// MUSE S hint colour:
///   PieceHintSystem calls SetHintColor(color, blend) when cognitive overload
///   is detected. The ghost tints to match its linked piece's hint colour so
///   the player can immediately see which zone the highlighted piece belongs to.
public class MagneticSnapZone : MonoBehaviour
{
    [Header("Linked Piece")]
    public PuzzlePiece linkedPiece;

    [Header("Ghost Visual")]
    public Renderer ghostRenderer;
    public Material ghostIdleMaterial;
    public Material ghostActiveMaterial;

    [Header("Proximity")]
    [Tooltip("Distance (m) at which the ghost brightens. Should match PuzzlePiece.magnetRange.")]
    public float activationRange = 0.15f;

    public bool IsSolved => _isSolved;

    private bool     _isSolved;
    private Material _ghostMat;          // per-instance copy owned by this zone

    private Color    _baseIdleColor;
    private Color    _baseActiveColor;
    private float    _currentIdleAlpha;
    private float    _currentActiveAlpha;

    private Color    _hintColor = Color.white;
    private float    _hintBlend = 0f;

    private void Awake()
    {
        if (ghostRenderer != null)
            _ghostMat = ghostRenderer.material;  // creates per-instance copy

        _baseIdleColor   = ghostIdleMaterial   != null ? ghostIdleMaterial.color   : new Color(0.65f, 0.80f, 1.00f, 0.20f);
        _baseActiveColor = ghostActiveMaterial != null ? ghostActiveMaterial.color : new Color(0.45f, 0.88f, 1.00f, 0.45f);
        _currentIdleAlpha   = _baseIdleColor.a;
        _currentActiveAlpha = _baseActiveColor.a;
    }

    private void OnEnable()
    {
        if (linkedPiece != null)
            linkedPiece.OnPieceSolved += HandlePieceSolved;
        _isSolved = false;
        SetGhostVisible(true);
    }

    private void OnDisable()
    {
        if (linkedPiece != null)
            linkedPiece.OnPieceSolved -= HandlePieceSolved;
    }

    private void Update()
    {
        if (_isSolved || _ghostMat == null || linkedPiece == null) return;

        float dist   = Vector3.Distance(linkedPiece.transform.position, transform.position);
        bool  isNear = dist <= activationRange;

        Color  baseCol   = isNear ? _baseActiveColor : _baseIdleColor;
        float  alpha     = isNear ? _currentActiveAlpha : _currentIdleAlpha;

        // Blend base ghost colour with hint colour; keep alpha
        Color full  = new Color(baseCol.r, baseCol.g, baseCol.b, alpha);
        Color hint  = new Color(_hintColor.r, _hintColor.g, _hintColor.b, alpha);
        _ghostMat.color = Color.Lerp(full, hint, _hintBlend);
    }

    // ── Difficulty control ────────────────────────────────────────────────────

    /// Set ghost silhouette transparency for the current difficulty.
    public void SetGhostAlpha(float idle, float active)
    {
        _currentIdleAlpha   = idle;
        _currentActiveAlpha = active;
    }

    // ── MUSE S hint colour ────────────────────────────────────────────────────

    /// blend=0 → base ghost colour. blend=1 → full hint colour tint.
    public void SetHintColor(Color hint, float blend)
    {
        _hintColor = hint;
        _hintBlend = Mathf.Clamp01(blend);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void HandlePieceSolved()
    {
        _isSolved = true;
        SetGhostVisible(false);
    }

    private void SetGhostVisible(bool visible)
    {
        if (ghostRenderer != null)
            ghostRenderer.enabled = visible;
    }
}
