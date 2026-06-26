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

    [Header("Stress-reactive cone (brightness is NOT touched — the beam keeps the Light's fixed intensity)")]
    [Tooltip("Spot angle (deg) when calm — a tighter, focused cone. Kept wide enough that there is " +
             "always a usable pool of light so the player is never completely lost.")]
    public float baseSpotAngle = 30f;
    [Tooltip("Spot angle (deg) when very stressed — wider so the search pool grows.")]
    public float maxSpotAngle = 60f;
    [Tooltip("How fast (deg/sec) the cone eases toward its stress-driven width. Low = a slow, gentle " +
             "open/close that doesn't snap with every stress flicker.")]
    public float coneSlewSpeed = 5f;

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

        // Not lit yet (lantern not grabbed) → nothing to drive; re-arm the one-shot cue so it
        // announces again the next time the beam comes on.
        if (beam == null || !beam.enabled) { _coneWideReported = false; return; }

        // Stress only ever re-shapes the CONE WIDTH — never the brightness. The beam keeps the fixed
        // intensity set on its Light, so there is ALWAYS a minimum pool of light and the player can
        // never be plunged into near-darkness when they calm down (the old "couldn't see anything once
        // my stress dropped" bug came from also dimming the beam). When the Muse helper is off, the
        // target is simply the base (calm) width.
        float targetAngle = baseSpotAngle;
        if (AssistanceSettings.MuseHelperEnabled && CognitiveLoadAdapter.Instance != null)
            targetAngle = Mathf.Lerp(baseSpotAngle, maxSpotAngle, CognitiveLoadAdapter.Instance.StressLevel);

        // Ease toward that width SLOWLY (coneSlewSpeed deg/sec) so it opens/closes gently instead of
        // snapping with every stress flicker.
        beam.spotAngle = Mathf.MoveTowards(beam.spotAngle, targetAngle, coneSlewSpeed * Time.deltaTime);

        // Announce ONCE while the lantern is lit and the Muse helper is driving the cone; re-arms when
        // the beam goes off or the Muse helper is toggled off.
        if (AssistanceSettings.MuseHelperEnabled)
        {
            if (!_coneWideReported)
            {
                AdaptiveEventBus.Report("Aligning lantern strength with your stress levels.",
                                        AdaptiveSignal.MuseStress);
                _coneWideReported = true;
            }
        }
        else _coneWideReported = false;
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
