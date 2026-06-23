using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// A grabbable lantern for the dark (hard) puzzle. The moment the player grabs it, it
/// clips to the GRABBING hand's forearm and STAYS there — freeing that hand to pick up
/// puzzle pieces — while its spotlight keeps pointing along the arm so the player lights
/// up whatever they reach toward.
///
/// Setup (done by the scene builder): GameObject with a mesh, a spotlight, a Collider, a
/// Rigidbody and an XRGrabInteractable, plus this component. Starts inactive; the
/// HardModeDarkroom enables it only on the hard difficulty.
[RequireComponent(typeof(XRGrabInteractable))]
public class ArmLantern : MonoBehaviour
{
    [Tooltip("Local offset from the hand toward the wrist/forearm once attached.")]
    public Vector3 forearmOffset = new Vector3(0f, 0.03f, -0.10f);

    [Tooltip("Local euler tilt so the spotlight aims slightly ahead of the arm.")]
    public Vector3 aimTilt = new Vector3(25f, 0f, 0f);

    private XRGrabInteractable _grab;
    private Rigidbody _rb;
    private bool _attached;

    private void Awake()
    {
        _grab = GetComponent<XRGrabInteractable>();
        _rb   = GetComponent<Rigidbody>();
    }

    private void OnEnable()  => _grab.selectEntered.AddListener(OnGrabbed);
    private void OnDisable() => _grab.selectEntered.RemoveListener(OnGrabbed);

    private void OnGrabbed(SelectEnterEventArgs args)
    {
        if (_attached) return;
        AttachToArm(args.interactorObject.transform);
    }

    private void AttachToArm(Transform hand)
    {
        _attached = true;

        // Park it on the forearm of the grabbing hand and stop physics owning it.
        transform.SetParent(hand, worldPositionStays: false);
        transform.localPosition = forearmOffset;
        transform.localRotation = Quaternion.Euler(aimTilt);

        if (_rb != null) { _rb.isKinematic = true; _rb.useGravity = false; }
        foreach (var col in GetComponentsInChildren<Collider>()) col.enabled = false;

        // Release it from the grab interaction so the hand is immediately free to grab
        // pieces, and disable further grabbing so it can never be dropped.
        _grab.enabled = false;
    }
}
