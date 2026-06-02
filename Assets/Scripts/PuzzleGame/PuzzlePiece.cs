using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// Attach to each individual puzzle piece GameObject.
/// Requires: XRGrabInteractable (VR grabbing), Rigidbody, Collider.
///
/// Difficulty visibility:
///   PuzzleManager calls SetVisibility(0–1) on each piece.
///   1.0 = full piece colour   (Easy — clearly visible)
///   0.0 = same grey as the zen wall (Hard — near-invisible)
///
/// MUSE S hint colour:
///   PieceHintSystem calls SetHintColor(color, blend) when cognitive overload
///   is detected. blend=0 means normal visibility; blend=1 means full hint colour.
///   The piece and its matching snap zone show the same colour, guiding the player.
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable))]
public class PuzzlePiece : MonoBehaviour
{
    [Header("Correct Placement")]
    [Tooltip("The Transform where this piece must land to be considered solved")]
    public Transform correctPlacementTarget;

    [Tooltip("How close (metres) the piece centre must be to snap as solved")]
    public float solveThreshold = 0.05f;

    [Header("Magnetic Snap")]
    [HideInInspector] public bool  isMagneticEnabled = false;
    [HideInInspector] public float magnetForce       = 5f;
    [HideInInspector] public float magnetRange       = 0.15f;

    [Header("Visual Feedback")]
    [Tooltip("Material swapped in when the piece is correctly placed")]
    public Material solvedMaterial;

    /// Fires once when this piece reaches its correct placement.
    public event Action OnPieceSolved;

    public bool IsSolved => _isSolved;

    // Grey that matches the zen room walls — pieces blend towards this on Hard
    private static readonly Color k_ZenGrey = new Color(0.87f, 0.87f, 0.87f);

    private Rigidbody   _rb;
    private UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable _grab;
    private Renderer    _renderer;
    private Material    _matInstance;   // per-piece material instance
    private Color       _baseColor;     // original colour from the material asset
    private float       _currentBrightness = 1f;
    private Color       _hintColor         = Color.white;
    private float       _hintBlend         = 0f;
    private bool        _isSolved          = false;
    private bool        _isBeingHeld       = false;

    private void Awake()
    {
        _rb       = GetComponent<Rigidbody>();
        _grab     = GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
        _renderer = GetComponentInChildren<Renderer>();

        if (_renderer != null)
        {
            _matInstance = _renderer.material;  // Unity creates a per-instance copy here
            _baseColor   = _matInstance.color;
        }
    }

    private void OnEnable()
    {
        _grab.selectEntered.AddListener(OnGrabbed);
        _grab.selectExited.AddListener(OnReleased);
    }

    private void OnDisable()
    {
        _grab.selectEntered.RemoveListener(OnGrabbed);
        _grab.selectExited.RemoveListener(OnReleased);
    }

    private void OnGrabbed(SelectEnterEventArgs _) => _isBeingHeld = true;
    private void OnReleased(SelectExitEventArgs _)
    {
        _isBeingHeld = false;
        CheckIfSolved();
    }

    private void FixedUpdate()
    {
        if (_isSolved || _isBeingHeld || correctPlacementTarget == null) return;
        float dist = Vector3.Distance(transform.position, correctPlacementTarget.position);
        if (isMagneticEnabled && dist < magnetRange)
        {
            Vector3 dir = (correctPlacementTarget.position - transform.position).normalized;
            _rb.AddForce(dir * magnetForce, ForceMode.Acceleration);
        }
    }

    // ── Difficulty visibility ─────────────────────────────────────────────────

    /// brightness=1 → full piece colour (Easy).
    /// brightness=0 → same grey as the zen wall (Hard — piece nearly invisible).
    public void SetVisibility(float brightness)
    {
        _currentBrightness = Mathf.Clamp01(brightness);
        UpdateMaterialColor();
    }

    // ── MUSE S hint colour ────────────────────────────────────────────────────

    /// blend=0 → normal visibility colour.
    /// blend=1 → full hint colour (piece and its matching snap zone share the same hue).
    public void SetHintColor(Color hint, float blend)
    {
        _hintColor = hint;
        _hintBlend = Mathf.Clamp01(blend);
        UpdateMaterialColor();
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void UpdateMaterialColor()
    {
        if (_matInstance == null || _isSolved) return;
        Color visColor = Color.Lerp(k_ZenGrey, _baseColor, _currentBrightness);
        Color final    = Color.Lerp(visColor, _hintColor, _hintBlend);
        _matInstance.color = final;
    }

    private void CheckIfSolved()
    {
        if (_isSolved || correctPlacementTarget == null) return;
        if (Vector3.Distance(transform.position, correctPlacementTarget.position) <= solveThreshold)
            MarkAsSolved();
    }

    private void MarkAsSolved()
    {
        _isSolved = true;
        transform.position  = correctPlacementTarget.position;
        transform.rotation  = correctPlacementTarget.rotation;
        _rb.isKinematic     = true;
        _grab.enabled       = false;

        if (solvedMaterial != null && _renderer != null)
            _renderer.material = solvedMaterial;

        Debug.Log($"[PuzzlePiece] '{name}' solved!");
        OnPieceSolved?.Invoke();
    }
}
