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
    public Vector3 aimTilt = new Vector3(10f, 0f, 0f);

    [Tooltip("The lantern's beam (spotlight). Stays OFF until grabbed, then lights the arm direction.")]
    public Light beam;

    [Header("Stress-reactive cone")]
    [Tooltip("Spot angle (deg) when the player is calm — a tight, focused cone that doesn't wash the room.")]
    public float baseSpotAngle = 34f;
    [Tooltip("Spot angle (deg) when the player is very stressed — a wider, easier-to-search cone.")]
    public float maxSpotAngle = 60f;

    private XRGrabInteractable _grab;
    private Rigidbody _rb;
    private bool _attached;
    private Transform _pendingHand;
    private bool _coneWideReported;

    private void Awake()
    {
        _grab = GetComponent<XRGrabInteractable>();
        _rb   = GetComponent<Rigidbody>();
        // Beam is dark until the player actually grabs the lantern — before that the
        // room stays pitch black and only the (emissive) lantern body is visible.
        if (beam != null) beam.enabled = false;
    }

    private void OnEnable()  => _grab.selectEntered.AddListener(OnGrabbed);
    private void OnDisable() => _grab.selectEntered.RemoveListener(OnGrabbed);

    private void OnGrabbed(SelectEnterEventArgs args)
    {
        // Defer the actual attach by a frame so XRI finishes its own grab handling first.
        if (!_attached) _pendingHand = args.interactorObject.transform;
        Debug.Log($"[ArmLantern] grabbed by '{_pendingHand?.name}' (beam wired: {beam != null})");
    }

    private void LateUpdate()
    {
        if (_pendingHand != null && !_attached)
        {
            AttachToArm(_pendingHand);
            _pendingHand = null;
        }

        // While lit, widen the cone with the player's stress so a very stressed player gets a
        // bigger pool of light to search by.
        if (beam != null && beam.enabled && CognitiveLoadAdapter.Instance != null)
        {
            float stress = CognitiveLoadAdapter.Instance.StressLevel;
            beam.spotAngle = Mathf.Lerp(baseSpotAngle, maxSpotAngle, stress);

            // Report the cone-widening action once per high-stress episode (hysteresis).
            if (!_coneWideReported && stress >= 0.70f)
            {
                AdaptiveEventBus.Report("Lantern cone widened to help you search", AdaptiveSignal.MuseStress);
                _coneWideReported = true;
            }
            else if (_coneWideReported && stress <= 0.50f)
            {
                _coneWideReported = false;
            }
        }
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

        // Now light up — the beam reveals whatever the arm points at.
        if (beam != null) beam.enabled = true;
        Debug.Log($"[ArmLantern] attached to '{hand.name}' forearm; beam on: {beam != null}");

        // Release it from the grab interaction so the hand is immediately free to grab
        // pieces, and disable further grabbing so it can never be dropped.
        _grab.enabled = false;
    }
}
