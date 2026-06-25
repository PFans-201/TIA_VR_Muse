using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// Attach to each individual puzzle piece GameObject.
/// Requires: XRGrabInteractable (VR grabbing), Rigidbody, Collider.
///
/// Magnetic snap (de-oscillating):
///   While not held and within magnetRange the piece is drawn toward its slot with a
///   critically-damped SmoothDamp move (position) plus a Slerp toward the slot rotation.
///   This replaces the old AddForce pull that overshot and orbited the target ("oscillating
///   around a weird axis"). It auto-solves the moment it is close enough.
///   On Easy, magnetWhileHeld lets the piece snap straight out of the hand into the slot.
///
/// Difficulty visibility:
///   PuzzleManager calls SetVisibility(0–1) on each piece.
///   1.0 = full piece colour   (Easy — clearly visible)
///   0.0 = same grey as the zen wall (Hard — near-invisible)
///
/// MUSE S / behaviour hint colour:
///   PieceHintSystem calls SetHintColor(color, blend) when the player is overloaded or has
///   been struggling with this piece. blend=0 means normal visibility; blend=1 means full
///   hint colour. The piece and its matching snap zone show the same colour, guiding placement.
///   SetGlow(true) makes a lost piece flicker so it can be found in a dark room.
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
    [HideInInspector] public float magnetForce       = 5f;     // pull strength (→ shorter smooth time)
    [HideInInspector] public float magnetRange       = 0.15f;
    [Tooltip("Easy mode: pull/snap the piece into its slot even while it is still being held.")]
    [HideInInspector] public bool  magnetWhileHeld   = false;

    [Header("Visual Feedback")]
    [Tooltip("Material swapped in when the piece is correctly placed")]
    public Material solvedMaterial;

    /// Fires once when this piece reaches its correct placement.
    public event Action OnPieceSolved;

    public bool IsSolved => _isSolved;

    // ── Behaviour instrumentation (read by PieceHintSystem) ───────────────────
    /// Seconds this piece has existed unsolved since the puzzle started.
    public float UnsolvedSeconds  { get; private set; }
    /// Number of times the player has grabbed this piece.
    public int   HeldCount        { get; private set; }
    /// Total seconds the player has held this piece.
    public float TotalHeldSeconds { get; private set; }
    public bool  IsHeld           => _isBeingHeld;

    // Grey that matches the zen room walls — pieces blend towards this on Hard
    private static readonly Color k_ZenGrey = new Color(0.87f, 0.87f, 0.87f);

    // Neutral grey a piece turns when correctly PLACED, so finished pieces recede and the
    // remaining (still-coloured) pieces stand out. Used when no solvedMaterial is assigned.
    private static readonly Color k_PlacedGrey = new Color(0.55f, 0.55f, 0.57f);

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
    private Vector3     _magnetVel         = Vector3.zero;   // SmoothDamp velocity ref

    [Header("Find-me Glow (dark rooms)")]
    [Tooltip("Flicker frequency (Hz) of the lost-piece glow.")]
    public float glowFrequency = 2.5f;
    private bool _glow;

    private void Awake()
    {
        _rb       = GetComponent<Rigidbody>();
        _grab     = GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
        _renderer = GetComponentInChildren<Renderer>();

        if (_renderer != null)
        {
            _matInstance = _renderer.material;  // Unity creates a per-instance copy here
            _baseColor   = _matInstance.color;
            _matInstance.EnableKeyword("_EMISSION");
            _matInstance.SetColor("_EmissionColor", Color.black);
        }
    }

    private void OnEnable()
    {
        _grab.selectEntered.AddListener(OnGrabbed);
        _grab.selectExited.AddListener(OnReleased);

        // Reset per-puzzle behaviour counters whenever this piece is (re)activated.
        UnsolvedSeconds  = 0f;
        HeldCount        = 0;
        TotalHeldSeconds = 0f;
        _magnetVel       = Vector3.zero;
        SetGlow(false);

        // Reset the SOLVED state too — a piece reused on a puzzle restart must behave like new.
        // MarkAsSolved disables the piece's colliders + grab and makes it kinematic; without this,
        // a previously-solved piece comes back with no colliders and falls through the table/floor
        // (the "2nd try, pieces fell through the block" bug), and can't be grabbed.
        _isSolved    = false;
        _isBeingHeld = false;
        if (_grab != null) _grab.enabled = true;
        if (_rb   != null) { _rb.isKinematic = false; _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero; }
        foreach (var col in GetComponentsInChildren<Collider>()) col.enabled = true;
        if (_renderer != null && _matInstance != null)
        {
            _renderer.material = _matInstance;   // restore the coloured instance (MarkAsSolved swapped it)
            UpdateMaterialColor();
        }
    }

    private void OnDisable()
    {
        _grab.selectEntered.RemoveListener(OnGrabbed);
        _grab.selectExited.RemoveListener(OnReleased);
    }

    private void OnGrabbed(SelectEnterEventArgs _) { _isBeingHeld = true; HeldCount++; }
    private void OnReleased(SelectExitEventArgs _)
    {
        _isBeingHeld = false;
        CheckIfSolved();
    }

    private void Update()
    {
        if (!_isSolved)
        {
            UnsolvedSeconds += Time.deltaTime;
            if (_isBeingHeld) TotalHeldSeconds += Time.deltaTime;
        }

        if (_glow && _matInstance != null)
        {
            float p = Mathf.Sin(Time.time * glowFrequency * Mathf.PI * 2f) * 0.5f + 0.5f;
            Color glowCol = _hintColor == Color.white ? new Color(1f, 0.85f, 0.4f) : _hintColor;
            _matInstance.SetColor("_EmissionColor", glowCol * Mathf.Lerp(0.15f, 2.2f, p));
        }
    }

    private void FixedUpdate()
    {
        if (_isSolved || correctPlacementTarget == null) return;

        float dist = Vector3.Distance(transform.position, correctPlacementTarget.position);

        if (_isBeingHeld)
        {
            // Easy: let the held piece snap straight into the slot once it enters the field.
            if (magnetWhileHeld && isMagneticEnabled && dist <= magnetRange)
                MarkAsSolved();
            return;
        }

        if (!isMagneticEnabled || dist > magnetRange) return;

        // Critically-damped pull — no overshoot, no orbiting. Stronger magnetForce → shorter
        // smooth time (snappier). Drive the body kinematically within the field so gravity and
        // residual velocity can't make it oscillate around the ghost.
        float smoothTime = Mathf.Clamp(1.2f / Mathf.Max(magnetForce, 0.01f), 0.06f, 0.6f);
        Vector3 newPos = Vector3.SmoothDamp(transform.position, correctPlacementTarget.position,
                                            ref _magnetVel, smoothTime, 6f, Time.fixedDeltaTime);
        _rb.MovePosition(newPos);
        _rb.MoveRotation(Quaternion.Slerp(transform.rotation, correctPlacementTarget.rotation,
                                          1f - Mathf.Exp(-12f * Time.fixedDeltaTime)));
        _rb.linearVelocity  = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;

        if (dist <= solveThreshold) MarkAsSolved();
    }

    // ── Difficulty visibility ─────────────────────────────────────────────────

    /// brightness=1 → full piece colour (Easy).
    /// brightness=0 → same grey as the zen wall (Hard — piece nearly invisible).
    public void SetVisibility(float brightness)
    {
        _currentBrightness = Mathf.Clamp01(brightness);
        UpdateMaterialColor();
    }

    // ── MUSE S / behaviour hint colour ────────────────────────────────────────

    /// blend=0 → normal visibility colour.
    /// blend=1 → full hint colour (piece and its matching snap zone share the same hue).
    public void SetHintColor(Color hint, float blend)
    {
        _hintColor = hint;
        _hintBlend = Mathf.Clamp01(blend);
        UpdateMaterialColor();
    }

    /// Flickering emissive glow so a piece lost in a dark room can be found.
    public void SetGlow(bool on)
    {
        _glow = on;
        if (!on && _matInstance != null)
            _matInstance.SetColor("_EmissionColor", Color.black);
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
        SetGlow(false);
        transform.position  = correctPlacementTarget.position;
        transform.rotation  = correctPlacementTarget.rotation;
        _rb.isKinematic     = true;
        _grab.enabled       = false;

        // Drop collisions on the placed piece so a physics-tracked held piece (and the magnet)
        // can nest the remaining pieces flush against the assembly instead of being blocked.
        foreach (var col in GetComponentsInChildren<Collider>()) col.enabled = false;

        // Placed pieces go grey (coloured = still to place). Use the assigned solvedMaterial if
        // there is one, otherwise just tint this piece's own material grey so prefab pieces don't
        // need a dedicated material.
        if (solvedMaterial != null && _renderer != null)
            _renderer.material = solvedMaterial;
        else if (_matInstance != null)
        {
            _matInstance.SetColor("_EmissionColor", Color.black);
            _matInstance.color = k_PlacedGrey;
        }

        Debug.Log($"[PuzzlePiece] '{name}' solved!");
        OnPieceSolved?.Invoke();
    }
}
