using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// Attach to each individual puzzle piece GameObject.
/// Requires: XRGrabInteractable (for VR grabbing), Rigidbody, Collider.
/// 
/// Setup in Unity Editor:
///   1. Create a 3D child object under your puzzle model (e.g. "BearHead", "BearLeftArm").
///   2. Add: Rigidbody, BoxCollider (or MeshCollider), XRGrabInteractable, PuzzlePiece.
///   3. Set "correctPosition" and "correctRotation" to match where the piece
///      should land when correctly placed (relative to the puzzle anchor).
///   4. Set "snapZone" to the matching MagneticSnapZone for this piece.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable))]
public class PuzzlePiece : MonoBehaviour
{
    [Header("Correct Placement")]
    [Tooltip("The world position where this piece is correctly solved")]
    public Transform correctPlacementTarget;

    [Tooltip("How close (meters) the piece must be to snap/register as solved")]
    public float solveThreshold = 0.05f;

    [Header("Magnetic Snap")]
    [Tooltip("Controlled by PuzzleManager based on difficulty")]
    [HideInInspector] public bool isMagneticEnabled = false;

    [Tooltip("Magnetic pull force applied when near the snap zone")]
    public float magnetForce = 5f;

    [Tooltip("Distance at which the magnet starts pulling")]
    public float magnetRange = 0.15f;

    [Header("Visual Feedback")]
    [Tooltip("Material applied when piece is correctly placed")]
    public Material solvedMaterial;

    /// <summary>Fires once when this piece is correctly placed.</summary>
    public event Action OnPieceSolved;

    private Rigidbody _rb;
    private UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable _grabInteractable;
    private Renderer _renderer;
    private Material _originalMaterial;
    private bool _isSolved = false;
    private bool _isBeingHeld = false;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _grabInteractable = GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
        _renderer = GetComponentInChildren<Renderer>();

        if (_renderer != null)
            _originalMaterial = _renderer.material;
    }

    private void OnEnable()
    {
        _grabInteractable.selectEntered.AddListener(OnGrabbed);
        _grabInteractable.selectExited.AddListener(OnReleased);
    }

    private void OnDisable()
    {
        _grabInteractable.selectEntered.RemoveListener(OnGrabbed);
        _grabInteractable.selectExited.RemoveListener(OnReleased);
    }

    private void OnGrabbed(SelectEnterEventArgs args)  => _isBeingHeld = true;
    private void OnReleased(SelectExitEventArgs args)
    {
        _isBeingHeld = false;
        CheckIfSolved();
    }

    private void FixedUpdate()
    {
        if (_isSolved || _isBeingHeld || correctPlacementTarget == null) return;

        float distance = Vector3.Distance(transform.position, correctPlacementTarget.position);

        // Apply magnetic pull when enabled and in range
        if (isMagneticEnabled && distance < magnetRange)
        {
            Vector3 direction = (correctPlacementTarget.position - transform.position).normalized;
            _rb.AddForce(direction * magnetForce, ForceMode.Acceleration);
        }
    }

    private void CheckIfSolved()
    {
        if (_isSolved || correctPlacementTarget == null) return;

        float distance = Vector3.Distance(transform.position, correctPlacementTarget.position);
        if (distance <= solveThreshold)
            MarkAsSolved();
    }

    private void MarkAsSolved()
    {
        _isSolved = true;

        // Snap exactly into place
        transform.position = correctPlacementTarget.position;
        transform.rotation = correctPlacementTarget.rotation;

        // Freeze the piece so it doesn't drift
        _rb.isKinematic = true;
        _grabInteractable.enabled = false;

        // Visual feedback
        if (solvedMaterial != null && _renderer != null)
            _renderer.material = solvedMaterial;

        Debug.Log($"[PuzzlePiece] '{gameObject.name}' solved!");
        OnPieceSolved?.Invoke();
    }
}
