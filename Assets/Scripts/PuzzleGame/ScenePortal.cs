using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Attach to a door or portal GameObject with a Collider (set to Is Trigger).
/// When the XR player's camera/head enters the trigger, it loads the target scene.
/// 
/// Setup in Unity Editor:
///   1. Create a 3D object (e.g. a Cube scaled as a doorframe opening, or a Quad).
///   2. Attach a BoxCollider and check "Is Trigger".
///   3. Attach this script.
///   4. Set "targetSceneName" to "ZenPuzzleScene" in the Inspector.
///   5. Add "ZenPuzzleScene" to File > Build Settings > Scenes In Build.
/// </summary>
public class ScenePortal : MonoBehaviour
{
    [Tooltip("Name of the scene to load (must be added to Build Settings)")]
    public string targetSceneName = "ZenPuzzleScene";

    [Tooltip("Tag of the player's head/camera GameObject (default: MainCamera)")]
    public string playerHeadTag = "MainCamera";

    [Tooltip("Optional visual effect when portal is active (e.g. a particle system)")]
    public GameObject portalEffect;

    [Tooltip("Seconds to wait before loading (for fade or transition effect)")]
    public float transitionDelay = 0.5f;

    private bool _isTransitioning = false;

    private void OnTriggerEnter(Collider other)
    {
        if (_isTransitioning) return;

        // Trigger only when the player's head/camera walks through
        if (other.CompareTag(playerHeadTag))
        {
            _isTransitioning = true;
            if (portalEffect != null)
                portalEffect.SetActive(true);

            Invoke(nameof(LoadTargetScene), transitionDelay);
        }
    }

    private void LoadTargetScene()
    {
        if (!string.IsNullOrEmpty(targetSceneName))
            SceneManager.LoadScene(targetSceneName);
        else
            Debug.LogError("[ScenePortal] targetSceneName is not set!");
    }
}
