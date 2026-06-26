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

    // Dark, desaturated grey a piece turns when correctly PLACED. Finished pieces recede and read
    // as a single "settled" colour clearly distinct from the still-coloured free pieces. Being dark
    // it also bounces far less of the forearm-lantern spotlight back at the player (the diffuse
    // glare that was blinding them when placing pieces with the light arm).
    private static readonly Color k_PlacedGrey = new Color(0.28f, 0.28f, 0.30f);

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
            _renderer.material = _matInstance;   // re-point at the coloured instance
            UpdateMaterialColor();               // MarkAsSolved darkened it to placed-grey; restore the live colour
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
        if (correctPlacementTarget == null) return;

        // A solved piece is LOCKED to its slot — re-assert that lock every physics step.
        //
        // Why this is needed: a piece can be solved while still selected by the XR interactor
        // (TryHeldSnap — the Easy magnetWhileHeld snap and the Muse stress-snap). MarkAsSolved then
        // makes it kinematic AND disables its colliders (so the remaining pieces can nest flush).
        // But when the hand later releases — or because MarkAsSolved disabled the grab and XRI
        // force-cancels the selection — XRGrabInteractable.Detach() RESTORES the rigidbody to its
        // cached state (isKinematic = false) and applies the controller's throw velocity. That
        // revives the body, and since its colliders are off it falls straight THROUGH the pedestal
        // ("placed pieces aren't held, they fall through the block"). Pinning here defeats that
        // revival regardless of XRI's internal ordering or version.
        if (_isSolved)
        {
            if (_rb != null && !_rb.isKinematic)
            {
                _rb.linearVelocity  = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
                _rb.isKinematic     = true;
            }
            transform.SetPositionAndRotation(correctPlacementTarget.position,
                                             correctPlacementTarget.rotation);
            return;
        }

        float dist = Vector3.Distance(transform.position, correctPlacementTarget.position);

        if (_isBeingHeld) { TryHeldSnap(dist); return; }

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

    /// Snap a HELD piece into its slot when it is brought close enough.
    /// Easy (magnetWhileHeld): the held piece snaps the moment it enters the magnet field.
    private void TryHeldSnap(float dist)
    {
        if (isMagneticEnabled && magnetWhileHeld && dist <= magnetRange) MarkAsSolved();
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

        // Placed pieces all turn the SAME dark grey (k_PlacedGrey). This does two jobs:
        //   1. They read as one "settled/placed" colour, clearly distinct from the still-coloured
        //      free pieces, so the player can tell at a glance what's done vs. what's left.
        //   2. A dark surface bounces far less of the forearm-lantern spotlight back — most of the
        //      "blinding when placing a piece with the light arm" glare is diffuse reflection off a
        //      bright surface, so darkening the placed assembly cuts it at the source.
        // They must still never VANISH (the old flat-grey swap made easy pieces "disappear once
        // placed"), so each keeps a faint self-emission and stays enabled. Apply to EVERY renderer
        // because a prefab piece can be several child meshes.
        //
        // Placed pieces are also fully MATTED: glossy placed pieces threw a blinding specular
        // highlight straight back at the player, so we kill metallic/smoothness/specular outright.
        foreach (var r in GetComponentsInChildren<Renderer>())
        {
            if (r == null) continue;
            r.enabled = true;                       // never let a correctly-placed piece disappear
            ApplyPlacedLook(r.material);            // per-renderer instance
        }

        Debug.Log($"[PuzzlePiece] '{name}' solved!");
        OnPieceSolved?.Invoke();
    }

    /// Recolour one renderer's material to the uniform dark placed-grey and matte it fully so the
    /// forearm lantern can't glare off it. Keeps a barely-there self-emission so a placed piece is
    /// always visible (never "disappears once placed"), even in the dark hard room.
    private static void ApplyPlacedLook(Material m)
    {
        Color placed = k_PlacedGrey; placed.a = 1f;
        m.color = placed;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", placed);

        if (m.HasProperty("_Metallic"))   m.SetFloat("_Metallic", 0f);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);      // URP Lit
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0f);      // Standard
        if (m.HasProperty("_SpecularHighlights")) { m.SetFloat("_SpecularHighlights", 0f); m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF"); }
        if (m.HasProperty("_GlossyReflections"))  { m.SetFloat("_GlossyReflections", 0f);  m.EnableKeyword("_GLOSSYREFLECTIONS_OFF"); }

        m.EnableKeyword("_EMISSION");
        m.SetColor("_EmissionColor", placed * 0.04f);   // barely-there glow: visible, not a glare source
    }
}
