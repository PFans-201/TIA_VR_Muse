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
[DisallowMultipleComponent]
public class VelorexeBleTransport : MonoBehaviour, IMuseBleTransport
{
    [Tooltip("How long to scan for the headset, in seconds, before giving up.")]
    public int scanSeconds = 20;

    private string _deviceId;

#if UNITY_ANDROID && !UNITY_EDITOR
    private ConnectToDevice _connectCmd;
    private readonly List<SubscribeToCharacteristic> _subs = new List<SubscribeToCharacteristic>();
    private string _nameContains;
    private Action<string> _onFound;
    private bool _found;
    private int _permsPending;
#endif

    // ── IMuseBleTransport ─────────────────────────────────────────────────────
    public void StartScan(string nameContains, Action<string> onFound)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        _nameContains = (nameContains ?? string.Empty).ToLowerInvariant();
        _onFound = onFound;
        _found = false;
        // Keep the BLE stack alive across scene loads (MuseDirectAdapter persists too).
        DontDestroyOnLoad(BleManager.Instance.gameObject);
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
        _connectCmd = new ConnectToDevice(
            deviceId,
            _ => onConnected?.Invoke(),
            _ => onDisconnected?.Invoke());
        BleManager.Instance.QueueCommand(_connectCmd);
#endif
    }

    public void Subscribe(string serviceUuid, string characteristicUuid, Action<byte[]> onData)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        var sub = new SubscribeToCharacteristic(
            _deviceId, serviceUuid, characteristicUuid,
            bytes => onData?.Invoke(bytes),
            customGatt: true);
        _subs.Add(sub);
        BleManager.Instance.QueueCommand(sub);
#endif
    }

    public void WriteCommand(string serviceUuid, string characteristicUuid, byte[] data)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        BleManager.Instance.QueueCommand(new WriteToCharacteristic(
            _deviceId, serviceUuid, characteristicUuid, data, customGatt: true));
#endif
    }

    public void Disconnect()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        foreach (var s in _subs) { try { s.Unsubscribe(); } catch { } }
        _subs.Clear();
        try { _connectCmd?.Disconnect(); } catch { }
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    // ── Internals ─────────────────────────────────────────────────────────────
    private void OnDeviceDiscovered(string deviceAddress, string deviceName)
    {
        if (_found || string.IsNullOrEmpty(deviceName)) return;
        if (!deviceName.ToLowerInvariant().Contains(_nameContains)) return;
        _found = true;
        _onFound?.Invoke(deviceAddress);
    }

    private IEnumerator EnsurePermissionsThenScan()
    {
        yield return RequestBlePermissions();
        // DiscoverDevices takes the scan window in milliseconds; the callback yields
        // (deviceAddress, deviceName) — we match by name then stop reacting.
        BleManager.Instance.QueueCommand(
            new DiscoverDevices(OnDeviceDiscovered, scanSeconds * 1000));
    }

    private IEnumerator RequestBlePermissions()
    {
        var needed = new List<string>();
        if (AndroidApiLevel() >= 31)
        {
            AddIfMissing(needed, "android.permission.BLUETOOTH_SCAN");
            AddIfMissing(needed, "android.permission.BLUETOOTH_CONNECT");
        }
        else
        {
            // Pre-Android-12 (e.g. older Quest builds) needs location for BLE scan.
            AddIfMissing(needed, Permission.FineLocation);
        }
        if (needed.Count == 0) yield break;

        _permsPending = needed.Count;
        var cb = new PermissionCallbacks();
        cb.PermissionGranted              += _ => _permsPending--;
        cb.PermissionDenied               += _ => _permsPending--;
        cb.PermissionDeniedAndDontAskAgain += _ => _permsPending--;
        Permission.RequestUserPermissions(needed.ToArray(), cb);

        float t = 0f;
        yield return new WaitUntil(() => _permsPending <= 0 || (t += Time.unscaledDeltaTime) > 30f);
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
