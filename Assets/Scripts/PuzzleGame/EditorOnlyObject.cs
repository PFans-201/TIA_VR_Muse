using UnityEngine;

/// Destroys its GameObject in non-Editor (player) builds, so Editor-only test helpers
/// — like the XR Device Simulator — never run on the Quest even if left in the scene.
public class EditorOnlyObject : MonoBehaviour
{
    private void Awake()
    {
        if (!Application.isEditor) Destroy(gameObject);
    }
}
