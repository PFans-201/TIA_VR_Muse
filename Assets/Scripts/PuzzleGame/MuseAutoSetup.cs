using UnityEngine;

public class MuseAutoSetup : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void AutoSetup()
    {
        // Destroy any existing adapters to ensure a clean slate
        foreach (var old in FindObjectsOfType<MuseDirectAdapter>()) {
            GameObject.DestroyImmediate(old.gameObject);
        }

        Debug.Log("[MuseAutoSetup] Dynamically injecting Muse BLE stack into scene...");
        
        GameObject go = new GameObject("MuseDirect_Auto");
        GameObject.DontDestroyOnLoad(go);

        // ADD BLE TRANSPORT FIRST!
        // MuseDirectAdapter looks for IMuseBleTransport in its Awake() method.
        // If we add it after, Awake() will fail.
        go.AddComponent<VelorexeBleTransport>();
        go.AddComponent<MuseDirectAdapter>();
        go.AddComponent<MuseStatusHUD>();
        go.AddComponent<MuseSimpleConnectionHUD>();
        
        Debug.Log("[MuseAutoSetup] Success! MuseDirect injected.");
    }
}
