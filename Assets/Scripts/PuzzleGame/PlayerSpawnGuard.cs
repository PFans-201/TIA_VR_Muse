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

    private XROrigin _origin;
    private Vector3  _spawn;

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
        _origin = GetComponent<XROrigin>();
        _spawn  = transform.position;   // the rig's authored spawn position
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
