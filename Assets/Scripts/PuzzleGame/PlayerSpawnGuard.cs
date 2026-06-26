using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using Unity.XR.CoreUtils;

/// "Fix the floor / placement once and for all" — a proper RUNTIME version of the intent behind
/// debug-hud's editor hacks (FixFall / FixFloorWidth), so it actually ships to the Quest and works
/// in every scene without any manual menu step or scene regeneration.
///
/// It self-installs on every scene load and provides:
///   1. SAFETY FLOOR — a large invisible collider under the room so wherever the headset boundary
///      drops the player, there is always solid ground; they can never fall through the world.
///   2. RECOVERY — if the player ends up below a kill-plane (fell through) or absurdly far from the
///      spawn point (boundary mis-spawn), the play space is gently TRANSLATED back (never rotated).
///   3. FACE THE CONTENT — the Meta Quest recenter (and app startup) points "forward" at whatever
///      direction the player is PHYSICALLY facing, which is usually a wall, not the room. We hook the
///      tracking-origin-updated event (fires on startup AND every Quest recenter) and yaw the rig ONCE
///      so the camera looks the authored content direction. It's one-shot per recenter — nothing
///      rotates per-frame — so head tracking is free between recenters.
[DefaultExecutionOrder(50)]
public class PlayerSpawnGuard : MonoBehaviour
{
    [Tooltip("Y of the room floor surface (these rooms use y = 0).")]
    public float floorY = 0f;
    [Tooltip("If the camera drops below this Y it is treated as having fallen through the world.")]
    public float killPlaneY = -2f;
    [Tooltip("If the camera is further than this (m) from the spawn point on XZ, recentre it.")]
    public float maxRadius = 25f;
    [Tooltip("Camera height (m above floorY) to restore to after a fall.")]
    public float recoverHeight = 1.2f;
    [Tooltip("Half-size (m) of the invisible safety-floor collider.")]
    public float safetyFloorHalfSize = 500f;

    [Header("Spawn recenter on scene load")]
    [Tooltip("Recenter the play space to the authored spawn XZ a few frames after each scene loads.")]
    public bool recenterOnSceneLoad = true;
    [Tooltip("Face the player at the authored content direction (the menu/table) on app startup AND " +
             "every time they press the Meta Quest recenter button — which otherwise leaves them facing " +
             "whatever wall they were physically facing. One-shot per recenter; head tracking is free " +
             "in between.")]
    public bool faceContentOnRecenter = true;
    [Tooltip("Frames to wait after load for XR tracking to report a real camera pose before recentering.")]
    public int  recenterDelayFrames = 3;

    private XROrigin _origin;
    private Vector3  _spawn;
    private Vector3  _spawnForward;   // the rig's authored forward (= the intended facing, at the content)
    private readonly List<XRInputSubsystem> _xrInput = new();

    // ── Self-install: runs once on load, then for every scene ──────────────────
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        Install();
        SceneManager.sceneLoaded += (_, __) => Install();
    }

    private static void Install()
    {
        var origin = Object.FindFirstObjectByType<XROrigin>();
        if (origin == null) return;

        var guard = origin.GetComponent<PlayerSpawnGuard>();
        if (guard == null) guard = origin.gameObject.AddComponent<PlayerSpawnGuard>();
        guard.EnsureSafetyFloor();
    }

    private void EnsureSafetyFloor()
    {
        if (GameObject.Find("RuntimeSafetyFloor") != null) return;
        var go  = new GameObject("RuntimeSafetyFloor");
        go.transform.position = new Vector3(0f, floorY - 0.1f, 0f);   // top sits at floorY
        var col = go.AddComponent<BoxCollider>();
        col.size = new Vector3(safetyFloorHalfSize * 2f, 0.2f, safetyFloorHalfSize * 2f);
    }

    private void Awake()
    {
        _origin       = GetComponent<XROrigin>();
        _spawn        = transform.position;   // the rig's authored spawn position
        _spawnForward = transform.forward;    // the rig's authored facing (SpawnXRRig aimed it at the content)
    }

    private void OnEnable()
    {
        // Hook the Quest recenter / tracking-origin establishment. This fires on app startup AND every
        // time the player presses the Meta Quest recenter button — exactly when "forward" gets pointed
        // at a wall — so it's the right moment to re-face them at the content.
        if (faceContentOnRecenter)
        {
            SubsystemManager.GetSubsystems(_xrInput);
            foreach (var s in _xrInput)
            {
                s.trackingOriginUpdated -= HandleTrackingOriginUpdated;
                s.trackingOriginUpdated += HandleTrackingOriginUpdated;
            }
        }

        if (recenterOnSceneLoad) StartCoroutine(RecenterAfterLoad());
    }

    private void OnDisable()
    {
        foreach (var s in _xrInput)
            if (s != null) s.trackingOriginUpdated -= HandleTrackingOriginUpdated;
    }

    // Quest recenter pressed (or origin first established at startup). The recenter points the camera at
    // the player's physical facing — re-face it at the content instead. Deferred a couple of frames so
    // the recentred pose has actually applied before we read the camera's forward.
    private void HandleTrackingOriginUpdated(XRInputSubsystem _)
    {
        if (isActiveAndEnabled) StartCoroutine(FaceContentDeferred());
    }

    private IEnumerator FaceContentDeferred()
    {
        yield return null;
        yield return null;
        FaceContentNow();
    }

    /// Re-run the one-shot recenter on demand (e.g. the "Restart Puzzle" button). POSITION ONLY — the
    /// player is already turned toward the content, so we never re-rotate them here (a yaw would be a
    /// mid-session snap). Facing is handled by the Quest recenter hook above.
    public void RecenterNow()
    {
        if (isActiveAndEnabled) StartCoroutine(RecenterAfterLoad());
    }

    private IEnumerator RecenterAfterLoad()
    {
        // Let XR tracking settle so the camera reports a real pose before we translate.
        for (int i = 0; i < Mathf.Max(1, recenterDelayFrames); i++) yield return null;
        if (_origin == null || _origin.Camera == null) yield break;

        // XZ-only translation (height untouched → can't reintroduce the floor-sink).
        Vector3 cam = _origin.Camera.transform.position;
        _origin.MoveCameraToWorldLocation(new Vector3(_spawn.x, cam.y, _spawn.z));

        // Initial facing attempt. On a WARM scene transition (Intro→Tutorial→Game) the camera pose is
        // already valid, so this faces the content immediately. On a COLD app start the pose may not be
        // settled yet and this is a no-op — but the Quest recenter hook (HandleTrackingOriginUpdated)
        // fires once the orientation is actually established and faces the content then.
        FaceContentNow();
    }

    /// One-shot yaw: rotate the rig around the camera so the camera's flat forward points the authored
    /// content direction. Delta-based (camera-forward → authored forward), so it accounts for the live
    /// headset yaw and points the camera AT the content rather than snapping to a fixed world yaw.
    private void FaceContentNow()
    {
        if (!faceContentOnRecenter || _origin == null || _origin.Camera == null) return;
        Vector3 camFwd = _origin.Camera.transform.forward; camFwd.y = 0f;
        Vector3 want   = _spawnForward;                    want.y   = 0f;
        if (camFwd.sqrMagnitude > 1e-4f && want.sqrMagnitude > 1e-4f)
            _origin.RotateAroundCameraUsingOriginUp(
                Vector3.SignedAngle(camFwd.normalized, want.normalized, Vector3.up));
    }

    private void LateUpdate()
    {
        if (_origin == null || _origin.Camera == null) return;
        Vector3 cam = _origin.Camera.transform.position;

        bool fell   = cam.y < killPlaneY;
        float dx = cam.x - _spawn.x, dz = cam.z - _spawn.z;
        bool tooFar = dx * dx + dz * dz > maxRadius * maxRadius;

        if (!fell && !tooFar) return;   // normal play — never touch the rig (so head tracking is safe)

        // Translation-only recovery: slide the play space so the camera returns to the spawn XZ
        // (and to a safe height if they had fallen). No rotation → head tracking is unaffected.
        float targetY = fell ? floorY + recoverHeight : cam.y;
        _origin.MoveCameraToWorldLocation(new Vector3(_spawn.x, targetY, _spawn.z));
    }
}
