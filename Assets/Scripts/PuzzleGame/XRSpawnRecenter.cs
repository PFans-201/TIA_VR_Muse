using UnityEngine;
using Unity.XR.CoreUtils;

/// Snaps the XR rig so the player's HEAD (camera) starts at a defined spawn point,
/// facing a target, every time the scene loads.
///
/// Why: in VR the rig's authored transform only sets the play-space origin — the
/// headset's real-world position/orientation is applied on top. After a scene
/// transition (e.g. Tutorial → Game) the player's physical offset carries over, so a
/// freshly-placed rig can leave them standing on the wrong side of the room. Opening
/// the scene directly happens to work only because the player is centred at that
/// moment. Recentring on Start makes the spawn deterministic in both cases.
///
/// Put this on the XR Origin root (the scene builder adds it). Yaw-only rotation keeps
/// the horizon level; the camera height is preserved so we never yank the player up/down.
[DefaultExecutionOrder(100)]   // after XR Origin / tracking has initialised this frame
public class XRSpawnRecenter : MonoBehaviour
{
    [Tooltip("World position the player's head should start at (x/z used; y uses the live camera height).")]
    public Vector3 spawnPosition = new Vector3(0f, 0f, -3f);

    [Tooltip("World point the player should face on spawn (yaw only).")]
    public Vector3 faceTarget = Vector3.zero;

    [Tooltip("Also recenter whenever this component is re-enabled, not just on the first Start.")]
    public bool recenterOnEnable = true;

    private XROrigin _origin;

    private void Awake() => _origin = GetComponent<XROrigin>();

    private void Start() => Recenter();

    private void OnEnable()
    {
        if (recenterOnEnable && _origin != null) Recenter();
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
