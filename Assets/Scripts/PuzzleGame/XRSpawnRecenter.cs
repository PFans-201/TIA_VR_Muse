using UnityEngine;
using Unity.XR.CoreUtils;

/// INERT BY DEFAULT. This used to auto-recenter the XR rig every scene load, but on the Quest
/// that runtime manipulation of the XR Origin / camera fought head tracking (the view stopped
/// following the headset) and could leave the player buried in the floor. The known-good
/// branches (feature/muse-debug-hud, feature/muse-merge-best-of-both) just let the prefab's own
/// tracking drive the camera, so we do the same: this component does NOTHING automatically.
///
/// Recenter() is kept as a manual, opt-in action (e.g. wire it to a "Recenter View" button).
/// It is never called on its own.
public class XRSpawnRecenter : MonoBehaviour
{
    [Tooltip("World position the player's head should move to when Recenter() is called manually.")]
    public Vector3 spawnPosition = new Vector3(0f, 0f, -3f);

    [Tooltip("World point the player should face when Recenter() is called manually (yaw only).")]
    public Vector3 faceTarget = Vector3.zero;

    // Kept for serialization compatibility with scenes that already reference these fields.
    [HideInInspector] public int firstRecenterFrame = 2;
    [HideInInspector] public int lastRecenterFrame  = 8;

    private XROrigin _origin;

    /// Manual one-shot recenter. NOT called automatically — hook it to a UI button / input action
    /// if you want a recenter control. Yaw-only; preserves the live camera height.
    public void Recenter()
    {
        if (_origin == null) _origin = GetComponent<XROrigin>();
        if (_origin == null) _origin = FindFirstObjectByType<XROrigin>();
        if (_origin == null || _origin.Camera == null) return;

        Transform cam = _origin.Camera.transform;
        _origin.MoveCameraToWorldLocation(new Vector3(spawnPosition.x, cam.position.y, spawnPosition.z));

        Vector3 camForward = cam.forward;  camForward.y = 0f;
        Vector3 want       = faceTarget - spawnPosition; want.y = 0f;
        if (camForward.sqrMagnitude > 1e-4f && want.sqrMagnitude > 1e-4f)
            _origin.RotateAroundCameraUsingOriginUp(Vector3.SignedAngle(camForward, want, Vector3.up));
    }
}
