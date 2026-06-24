using UnityEngine;
using Unity.XR.CoreUtils;

/// Snaps the XR rig so the player's HEAD (camera) starts at a defined spawn point,
/// facing a target, every time the scene loads.
///
/// Why: in VR the rig's authored transform only sets the play-space origin — the
/// headset's real-world position/orientation is applied on top. After a scene
/// transition (e.g. Tutorial → Game) the player's physical offset carries over, so a
/// freshly-placed rig can leave them standing on the wrong side of the room.
///
/// CRITICAL TIMING: the recenter must run AFTER the XR tracking pose has been applied
/// to the camera for the frame — doing it in Start (before the HMD pose arrives) snaps
/// to a stale camera position and the real pose then displaces the player again. So we
/// recenter across the first few LateUpdates (once tracking is live) and then stop, so
/// the player is free to move afterwards.
///
/// Put this on the XR Origin root (the scene builder adds it). Yaw-only rotation keeps
/// the horizon level; the camera height is preserved so we never yank the player up/down.
[DefaultExecutionOrder(100)]
public class XRSpawnRecenter : MonoBehaviour
{
    [Tooltip("World position the player's head should start at (x/z used; y uses the live camera height).")]
    public Vector3 spawnPosition = new Vector3(0f, 0f, -3f);

    [Tooltip("World point the player should face on spawn (yaw only).")]
    public Vector3 faceTarget = Vector3.zero;

    [Tooltip("Recenter on these frames after enable, giving XR tracking time to provide a valid pose.")]
    public int firstRecenterFrame = 2;
    public int lastRecenterFrame  = 8;

    private XROrigin _origin;
    private int _frame;
    private bool _done;

    private void Awake()
    {
        _origin = GetComponent<XROrigin>();
        if (_origin != null)
        {
            _origin.RequestedTrackingOriginMode = Unity.XR.CoreUtils.XROrigin.TrackingOriginMode.Floor;
            _origin.CameraYOffset = 1.6f;
        }
    }

    private void OnEnable() { _frame = 0; _done = false; }

    private void LateUpdate()
    {
        if (_done) return;
        _frame++;
        if (_frame < firstRecenterFrame) return;   // let the HMD pose apply first
        Recenter();
        if (_frame >= lastRecenterFrame) _done = true;
    }

    /// Public so a "Recenter" button / input action can call it too.
    public void Recenter()
    {
        if (_origin == null) _origin = GetComponent<XROrigin>();
        if (_origin == null) _origin = FindFirstObjectByType<XROrigin>();
        if (_origin == null || _origin.Camera == null) return;

        Transform cam = _origin.Camera.transform;

        // 1) Move the rig so the camera sits over the spawn point (keep current height).
        _origin.MoveCameraToWorldLocation(new Vector3(spawnPosition.x, cam.position.y, spawnPosition.z));

        // 2) Yaw the rig so the (flattened) camera forward points at the target.
        Vector3 camForward = cam.forward;  camForward.y = 0f;
        Vector3 want       = faceTarget - spawnPosition; want.y = 0f;
        if (camForward.sqrMagnitude > 1e-4f && want.sqrMagnitude > 1e-4f)
        {
            float angle = Vector3.SignedAngle(camForward, want, Vector3.up);
            _origin.RotateAroundCameraUsingOriginUp(angle);
        }
    }
}
