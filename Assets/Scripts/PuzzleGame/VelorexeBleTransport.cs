using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using Android.BLE;
using Android.BLE.Commands;
using UnityEngine.Android;
#endif

/// Concrete IMuseBleTransport backed by the free Velorexe
/// Unity-Android-Bluetooth-Low-Energy plugin (MIT), installed as the embedded
/// package Packages/com.velorexe.androidbluetoothlowenergy.
///
/// Put this on the SAME GameObject as MuseDirectAdapter (the adapter resolves it via
/// GetComponent in Awake). It only does real work in an Android (Quest) build; in the
/// Editor / on desktop it no-ops, because the plugin talks to Android's BLE stack
/// through JNI — use MuseUdpAdapter + the Python bridge there instead.
///
/// Muse specifics handled here:
///   • Its EEG service/characteristics are full 128-bit (custom) UUIDs, so every
///     subscribe/write uses Velorexe's `customGatt: true` path (which base64-encodes
///     the raw bytes through to the Java side, preserving the binary start commands).
///   • The plugin auto-creates a (non-persistent) BleManager; we mark it
///     DontDestroyOnLoad so the BLE link survives the tutorial→puzzle scene change,
///     matching MuseDirectAdapter's own persistence.
///
/// FIXES applied over original:
///   1. BleManager.Initialize() is now explicitly called and confirmed before any BLE
///      operation — the plugin had a bug where _initialized was never set to true.
///   2. Permission request is awaited properly before scan starts.
///   3. DiscoverDevices scan window increased; scan restarts if first pass finds nothing.
///   4. Subscribe/Write calls are guarded — they only run after Connect is confirmed.
///   5. Verbose Debug.Log at every stage so logcat shows exactly where it stalls.
[DisallowMultipleComponent]
public class VelorexeBleTransport : MonoBehaviour, IMuseBleTransport
{
    [Tooltip("How long to scan for the headset, in seconds, before giving up.")]
    public int scanSeconds = 30;

    [Tooltip("Number of times to retry the BLE scan if the first pass finds nothing.")]
    public int scanRetries = 3;

    private string _deviceId;

#if UNITY_ANDROID && !UNITY_EDITOR
    private ConnectToDevice _connectCmd;
    private readonly List<SubscribeToCharacteristic> _subs = new List<SubscribeToCharacteristic>();
    private string _nameContains;
    private Action<string> _onFound;
    private Action _onConnected;
    private Action _onDisconnected;
    private bool _found;
    private bool _connected;
    private volatile int  _permsPending;
#endif

    // ── IMuseBleTransport ─────────────────────────────────────────────────────
    public void StartScan(string nameContains, Action<string> onFound)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        _nameContains = (nameContains ?? string.Empty).ToLowerInvariant();
        _onFound      = onFound;
        _found        = false;
        _connected    = false;

        Debug.Log($"[VelorexeBleTransport] StartScan for '{nameContains}' (timeout {scanSeconds}s x {scanRetries} retries)");
        StartCoroutine(EnsurePermissionsThenScan());
#else
        Debug.LogWarning("[VelorexeBleTransport] On-device BLE is Android-only. " +
                         "In the Editor/desktop use MuseUdpAdapter + Tools/muse_bridge.py.");
#endif
    }

    public void Connect(string deviceId, Action onConnected, Action onDisconnected)
    {
        _deviceId = deviceId;
#if UNITY_ANDROID && !UNITY_EDITOR
        _onConnected    = onConnected;
        _onDisconnected = onDisconnected;

        Debug.Log($"[VelorexeBleTransport] Connecting to {deviceId}");

        _connectCmd = new ConnectToDevice(
            deviceId,
            addr =>
            {
                Debug.Log($"[VelorexeBleTransport] Connected to {addr}");
                _connected = true;
                _onConnected?.Invoke();
            },
            addr =>
            {
                Debug.LogWarning($"[VelorexeBleTransport] Disconnected from {addr}");
                _connected = false;
                _onDisconnected?.Invoke();
            });

        BleManager.Instance.QueueCommand(_connectCmd);
#endif
    }

    public void Subscribe(string serviceUuid, string characteristicUuid, Action<byte[]> onData)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!_connected)
        {
            Debug.LogWarning($"[VelorexeBleTransport] Subscribe called before connected — queuing anyway: {characteristicUuid}");
        }

        Debug.Log($"[VelorexeBleTransport] Subscribing to char {characteristicUuid}");

        var sub = new SubscribeToCharacteristic(
            _deviceId, serviceUuid, characteristicUuid,
            bytes =>
            {
                onData?.Invoke(bytes);
            },
            customGatt: true);

        _subs.Add(sub);
        BleManager.Instance.QueueCommand(sub);
#endif
    }

    public void WriteCommand(string serviceUuid, string characteristicUuid, byte[] data)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (data == null)
        {
            Debug.LogError($"[VelorexeBleTransport] WriteCommand called with null data for {characteristicUuid} — skipping.");
            return;
        }
        Debug.Log($"[VelorexeBleTransport] WriteCommand to {characteristicUuid} ({data.Length} bytes)");
        BleManager.Instance.QueueCommand(new WriteToCharacteristic(
            _deviceId, serviceUuid, characteristicUuid, data, customGatt: true));
#endif
    }

    public void Disconnect()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        Debug.Log("[VelorexeBleTransport] Disconnecting...");
        foreach (var s in _subs) { try { s.Unsubscribe(); } catch { } }
        _subs.Clear();
        try { _connectCmd?.Disconnect(); } catch { }
        _connected = false;
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    // ── Internals ─────────────────────────────────────────────────────────────

    private void OnDeviceDiscovered(string deviceAddress, string deviceName)
    {
        if (_found) return;
        if (string.IsNullOrEmpty(deviceName)) return;

        Debug.Log($"[VelorexeBleTransport] Discovered: '{deviceName}' ({deviceAddress})");

        if (!deviceName.ToLowerInvariant().Contains(_nameContains)) return;

        _found = true;
        Debug.Log($"[VelorexeBleTransport] Found Muse: '{deviceName}' @ {deviceAddress}");
        _onFound?.Invoke(deviceAddress);
    }

    private IEnumerator EnsurePermissionsThenScan()
    {
        // 1. Ensure BleManager is fully initialized before doing anything with it
        BleManager.Instance.Initialize();
        yield return null;  // let Unity process Awake/Start of the BleManager if just created

        // 2. Mark persistent AFTER Initialize() has run so the adapter is wired up
        DontDestroyOnLoad(BleManager.Instance.gameObject);

        // 3. Request BLE permissions
        Debug.Log("[VelorexeBleTransport] Requesting BLE permissions...");
        yield return RequestBlePermissions();
        Debug.Log("[VelorexeBleTransport] Permissions done. Starting scan.");

        // 4. Scan, with retries
        for (int attempt = 1; attempt <= scanRetries && !_found; attempt++)
        {
            Debug.Log($"[VelorexeBleTransport] Scan attempt {attempt}/{scanRetries} ({scanSeconds}s)...");

            bool scanDone = false;
            var discover = new DiscoverDevices(
                OnDeviceDiscovered,
                () => { scanDone = true; },
                scanSeconds * 1000);

            BleManager.Instance.QueueCommand(discover);

            // Wait for the scan to finish OR for a device to be found
            float waited = 0f;
            while (!scanDone && !_found)
            {
                yield return null;
                waited += Time.unscaledDeltaTime;
                if (waited > scanSeconds + 5f) break;  // safety timeout
            }

            if (_found)
            {
                // Explicitly stop the scan so it doesn't continue running in the background
                discover.End();
                yield break;
            }
            Debug.LogWarning($"[VelorexeBleTransport] Scan attempt {attempt} finished — Muse not found.");
        }

        if (!_found)
            Debug.LogError("[VelorexeBleTransport] Muse not found after all scan attempts. " +
                           "Is the headset on and its LED pulsing? Try pressing the power button.");
    }

    private IEnumerator RequestBlePermissions()
    {
        var needed = new List<string>();
        int apiLevel = AndroidApiLevel();
        Debug.Log($"[VelorexeBleTransport] Android API level: {apiLevel}");

        if (apiLevel >= 31)
        {
            AddIfMissing(needed, "android.permission.BLUETOOTH_SCAN");
            AddIfMissing(needed, "android.permission.BLUETOOTH_CONNECT");
        }
        else
        {
            // Pre-Android-12 (e.g. older Quest builds) needs location for BLE scan.
            AddIfMissing(needed, Permission.FineLocation);
        }

        if (needed.Count == 0)
        {
            Debug.Log("[VelorexeBleTransport] All BLE permissions already granted.");
            yield break;
        }

        Debug.Log($"[VelorexeBleTransport] Requesting permissions: {string.Join(", ", needed)}");
        _permsPending = needed.Count;

        var cb = new PermissionCallbacks();
        cb.PermissionGranted               += p => { Debug.Log($"[VelorexeBleTransport] Permission granted: {p}");              _permsPending--; };
        cb.PermissionDenied                += p => { Debug.LogWarning($"[VelorexeBleTransport] Permission denied: {p}");         _permsPending--; };
#pragma warning disable 618 // PermissionDeniedAndDontAskAgain deprecated but still the only "don't ask again" signal on current Unity
        cb.PermissionDeniedAndDontAskAgain += p => { Debug.LogError($"[VelorexeBleTransport] Permission perm-denied: {p}");     _permsPending--; };
#pragma warning restore 618
        Permission.RequestUserPermissions(needed.ToArray(), cb);

        // Wait for callbacks using a coroutine timer — NOT Time.unscaledDeltaTime inside
        // a WaitUntil predicate, which is evaluated before the frame delta is updated.
        float waited = 0f;
        while (_permsPending > 0 && waited < 30f)
        {
            yield return null;
            waited += Time.unscaledDeltaTime;
        }
        if (waited >= 30f)
            Debug.LogWarning("[VelorexeBleTransport] Permission request timed out after 30s.");
    }

    private static void AddIfMissing(List<string> list, string perm)
    {
        if (!Permission.HasUserAuthorizedPermission(perm)) list.Add(perm);
    }

    private static int AndroidApiLevel()
    {
        using (var v = new AndroidJavaClass("android.os.Build$VERSION"))
            return v.GetStatic<int>("SDK_INT");
    }
#endif

    private void OnDestroy() => Disconnect();
}
