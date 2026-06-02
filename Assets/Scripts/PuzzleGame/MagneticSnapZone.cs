using UnityEngine;

/// <summary>
/// Attach to each snap target Transform — the same GameObject you assign as
/// "correctPlacementTarget" on a PuzzlePiece.
///
/// Responsibilities:
///   - Displays a ghost/silhouette mesh at the target position to guide the player.
///   - Switches to an "active" ghost material when the piece is close enough that
///     the magnetic force in PuzzlePiece is pulling it.
///   - Hides the ghost once the piece is correctly placed.
///
/// Setup in Unity Editor:
///   1. Create an empty child GameObject where the piece should land.
///      Match its position and rotation to the piece's solved pose.
///   2. Attach this script to that GameObject.
///   3. Assign that same GameObject as "correctPlacementTarget" on the matching PuzzlePiece.
///   4. Set "linkedPiece" to the matching PuzzlePiece.
///   5. Add a child MeshFilter + MeshRenderer (duplicate of the piece mesh) and assign
///      it as "ghostRenderer". Apply a semi-transparent material to it in the Editor.
///   6. Assign "ghostIdleMaterial"   — dim, semi-transparent (e.g. alpha ~0.2).
///      Assign "ghostActiveMaterial" — brighter/glowing (e.g. alpha ~0.5, emissive tint).
/// </summary>
public class MagneticSnapZone : MonoBehaviour
{
    [Header("Linked Piece")]
    [Tooltip("The PuzzlePiece whose correctPlacementTarget is this Transform.")]
    public PuzzlePiece linkedPiece;

    [Header("Ghost Visual")]
    [Tooltip("MeshRenderer of the ghost/silhouette child object.")]
    public Renderer ghostRenderer;

    [Tooltip("Dim semi-transparent material shown when the piece is far away.")]
    public Material ghostIdleMaterial;

    [Tooltip("Brighter/glowing material shown when the piece is within activation range.")]
    public Material ghostActiveMaterial;

    [Header("Proximity")]
    [Tooltip("Distance (meters) at which the ghost switches to the active material. " +
             "Should roughly match the magnetRange on PuzzlePiece.")]
    public float activationRange = 0.15f;

    // ── Private state ────────────────────────────────────────────────────────

    private bool _isSolved = false;

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void OnEnable()
    {
        if (linkedPiece != null)
            linkedPiece.OnPieceSolved += HandlePieceSolved;

        // Reset visual state when the zone is (re-)activated
        _isSolved = false;
        SetGhostVisible(true);
        ApplyGhostMaterial(ghostIdleMaterial);
    }

    private void OnDisable()
    {
        if (linkedPiece != null)
            linkedPiece.OnPieceSolved -= HandlePieceSolved;
    }

    private void Update()
    {
        if (_isSolved || ghostRenderer == null || linkedPiece == null) return;

        float distance = Vector3.Distance(linkedPiece.transform.position, transform.position);
        bool isNear = (distance <= activationRange);

        ApplyGhostMaterial(isNear ? ghostActiveMaterial : ghostIdleMaterial);
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    private void HandlePieceSolved()
    {
        _isSolved = true;
        SetGhostVisible(false);
        Debug.Log($"[MagneticSnapZone] '{gameObject.name}' — piece solved, ghost hidden.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetGhostVisible(bool visible)
    {
        if (ghostRenderer != null)
            ghostRenderer.enabled = visible;
    }

    private void ApplyGhostMaterial(Material mat)
    {
        if (ghostRenderer != null && mat != null)
            ghostRenderer.material = mat;
    }
}
