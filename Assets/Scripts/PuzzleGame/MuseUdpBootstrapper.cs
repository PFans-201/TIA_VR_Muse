using UnityEngine;

/// Auto-creates the MuseUdpAdapter singleton at startup so it persists across
/// all scenes without manually adding it to every scene in the Editor.
///
/// WHY THIS EXISTS
///   The WiFi bridge workflow (Mac → Quest over hotspot) needs MuseUdpAdapter
///   to be present in the scene, but the project's scenes only contain
///   MuseDirectAdapter (for the BLE path). Rather than requiring the user to
///   manually drag a new GameObject into every scene, this bootstrapper runs
///   before any scene loads and creates the adapter automatically.
///
/// USAGE
///   This script has a [RuntimeInitializeOnLoadMethod] attribute so it runs
///   automatically — no scene wiring needed. Just having this .cs file in
///   the project is enough.
///
/// CONFIGURATION
///   After the game starts, find the "MuseUdpAdapter" GameObject in the
///   Hierarchy (it persists via DontDestroyOnLoad) and set:
///     • bridgeHost = your Mac's IP on the hotspot (e.g. "172.20.10.2")
///     • port = 5005 (default, matches muse_lsl_bridge.py --udp-port)
///     • controlPort = 5006 (default, matches --control-port)
///
///   Alternatively, set defaults below before building.
public static class MuseUdpBootstrapper
{
    // ── Edit these before building to the Quest ──────────────────────────────
    // The IP address of your Mac on the iPhone hotspot network.
    // Find it via: System Settings → Wi-Fi → Details → IP Address
    private const string DefaultBridgeHost = "127.0.0.1";
    private const int    DefaultPort       = 5005;
    private const int    DefaultControlPort = 5006;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        // Don't create if one already exists (e.g. placed manually in a scene)
        if (MuseUdpAdapter.Instance != null) return;

        var go = new GameObject("MuseUdpAdapter");
        var adapter = go.AddComponent<MuseUdpAdapter>();
        adapter.bridgeHost  = DefaultBridgeHost;
        adapter.port        = DefaultPort;
        adapter.controlPort = DefaultControlPort;

        // DontDestroyOnLoad is already called in MuseUdpAdapter.Awake(),
        // but we call it here too in case Awake hasn't run yet.
        Object.DontDestroyOnLoad(go);

        Debug.Log($"[MuseUdpBootstrapper] Created MuseUdpAdapter " +
                  $"(bridgeHost={DefaultBridgeHost}, port={DefaultPort}, " +
                  $"controlPort={DefaultControlPort})");
    }
}
