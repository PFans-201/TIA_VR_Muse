using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.XR.CoreUtils;

/// "Fix the floor / placement once and for all" — a proper RUNTIME version of the intent behind
/// debug-hud's editor hacks (FixFall / FixFloorWidth), so it actually ships to the Quest and works
/// in every scene without any manual menu step or scene regeneration.
///
/// It self-installs on every scene load and provides two safety nets:
///   1. SAFETY FLOOR — a large invisible collider under the room so wherever the headset boundary
///      drops the player, there is always solid ground; they can never fall through the world.
///   2. RECOVERY — if the player still ends up below a kill-plane (fell through) or absurdly far
///      from the spawn point (boundary mis-spawn), the play space is gently TRANSLATED back.
///
/// CRITICAL: it does NOTHING during normal play — the recovery only fires on a real fall / far
/// spawn, and it only TRANSLATES the rig (never rotates, never per-frame snaps), so unlike the old
/// auto-recenter it cannot fight head tracking or bury the player.
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
    [Tooltip("Recenter the play space to the authored spawn XZ once, a few frames after each " +
             "scene loads — so the player no longer has to reload to fix their start position.")]
    public bool recenterOnSceneLoad = true;
    [Tooltip("Also yaw the play space ONCE on load so the player faces the authored forward (the " +
             "text panels / puzzle), regardless of which way the headset booted facing.\n" +
             "DEFAULT OFF: the rig is now authored already facing the content (SpawnXRRig rotates it " +
             "toward the room at build time), and rotating the play space at runtime fought head " +
             "tracking / sent the player to a side wall. Leave off unless a headset boots mis-yawed.")]
    public bool recenterFacingOnLoad = false;
    [Tooltip("Frames to wait after load for XR tracking to report a real camera pose before recentering.")]
    public int  recenterDelayFrames = 3;
    [Tooltip("Extra yaw (degrees) applied AFTER facing the authored forward, to correct a headset " +
             "that consistently boots ~90° off the text panels. Only used when recenterFacingOnLoad " +
             "is ON. DEFAULT 0: facing is now baked into the scene by SpawnXRRig, so no runtime yaw " +
             "is applied. Set recenterFacingOnLoad=true + ±90 only if a specific headset boots mis-yawed.")]
    public float spawnYawOffset = 0f;

    private XROrigin _origin;
    private Vector3  _spawn;
    private Vector3  _spawnForward;   // the rig's authored forward (the room's content direction)

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
        _spawnForward = transform.forward;    // the direction the room was built to be faced from
    }

    // One-shot recenter on (every) scene load: slide the play space so the camera sits at the
    // authored spawn XZ. This is the runtime equivalent of the manual reload the player used to
    // need, and it fixes a boundary mis-spawn that drops them away from the room content.
    private void OnEnable()
    {
        if (recenterOnSceneLoad) StartCoroutine(RecenterAfterLoad());
    }

    /// Re-run the one-shot recenter on demand (e.g. the "Restart Puzzle" button) so the player is
    /// slid back to the authored spawn XZ and faced toward the room content again, without a reload.
    public void RecenterNow()
    {
        if (isActiveAndEnabled) StartCoroutine(RecenterAfterLoad());
    }

    private IEnumerator RecenterAfterLoad()
    {
        // Let XR tracking settle so the camera reports a real pose before we translate.
        for (int i = 0; i < Mathf.Max(1, recenterDelayFrames); i++) yield return null;
        if (_origin == null || _origin.Camera == null) yield break;

        // XZ-only translation (height untouched → can't reintroduce the floor-sink). Same
        // primitive the fall-recovery uses.
        Vector3 cam = _origin.Camera.transform.position;
        _origin.MoveCameraToWorldLocation(new Vector3(_spawn.x, cam.y, _spawn.z));

        // ONE-SHOT yaw so the player faces the room's content (panels/puzzle) at the start,
        // whichever way the headset happened to boot facing. This rotates the rig around the
        // camera (camera position unchanged) exactly once — it never fights head tracking the
        // way a per-frame recenter would.
        if (recenterFacingOnLoad)
        {
            Vector3 camFwd = _origin.Camera.transform.forward; camFwd.y = 0f;
            Vector3 target = _spawnForward;                    target.y = 0f;
            if (camFwd.sqrMagnitude > 1e-4f && target.sqrMagnitude > 1e-4f)
            {
                float yaw = Vector3.SignedAngle(camFwd.normalized, target.normalized, Vector3.up);
                _origin.RotateAroundCameraUsingOriginUp(yaw);
            }

            // Fixed extra correction for a headset that boots a quarter-turn off the panels.
            if (Mathf.Abs(spawnYawOffset) > 0.01f)
                _origin.RotateAroundCameraUsingOriginUp(spawnYawOffset);
        }
        Debug.Log("[PlayerSpawnGuard] Recentered play space (position + facing) on scene load.");
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
