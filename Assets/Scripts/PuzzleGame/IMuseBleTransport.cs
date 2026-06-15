using System;

/// Minimal Bluetooth-LE seam so MuseDirectAdapter does not depend on any specific
/// BLE plugin. Implement this with whatever Android BLE plugin you ship on the Quest
/// (e.g. the free Velorexe Unity-Android-Bluetooth-Low-Energy) — only this one class
/// changes if you switch plugins. See docs/muse-unity-bridge.md for a Velorexe
/// implementation sketch.
///
/// Threading: MuseDirectAdapter assumes the callbacks may arrive on a background
/// thread and marshals safely (the signal processor's buffers are locked). If your
/// plugin already delivers on the main thread, that is fine too.
public interface IMuseBleTransport
{
    /// Scan for a device whose advertised name contains <paramref name="nameContains"/>
    /// (case-insensitive) and invoke <paramref name="onFound"/> once with an opaque
    /// device id (MAC on Android, UUID on iOS/macOS). Stop scanning after the match.
    void StartScan(string nameContains, Action<string> onFound);

    /// Connect to a previously-found device id.
    void Connect(string deviceId, Action onConnected, Action onDisconnected);

    /// Enable notifications on a characteristic; <paramref name="onData"/> receives each
    /// raw notification payload.
    void Subscribe(string serviceUuid, string characteristicUuid, Action<byte[]> onData);

    /// Write a command (no response) to a characteristic.
    void WriteCommand(string serviceUuid, string characteristicUuid, byte[] data);

    /// Tear down the connection.
    void Disconnect();
}
