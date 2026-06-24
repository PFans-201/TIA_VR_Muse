#if UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

/// Injects the Muse-on-Quest Bluetooth permissions into the Android manifest that Unity
/// GENERATES during the build, after the launcher activity is already in place.
///
/// Why a post-processor instead of Assets/Plugins/Android/AndroidManifest.xml: a file at
/// that path becomes the project's MAIN manifest and, if incomplete, breaks the launcher
/// activity (the Gradle "Missing 'name' key attribute on element activity" failure). And
/// the embedded velorexe package's library manifest is NOT merged by Unity's build, so the
/// permissions were silently missing from the APK. Editing the generated manifest here is
/// robust, headless-build friendly, and never clobbers Unity's launcher/OpenXR setup.
public class AndroidBlePermissions : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 1;

    const string AndroidNs = "http://schemas.android.com/apk/res/android";
    const string ToolsNs   = "http://schemas.android.com/tools";

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(manifestPath))
        {
            Debug.LogError($"[AndroidBlePermissions] Manifest not found: {manifestPath}");
            return;
        }

        var doc = new XmlDocument();
        doc.Load(manifestPath);
        var manifest = doc.DocumentElement;
        if (manifest == null) return;

        if (string.IsNullOrEmpty(manifest.GetAttribute("xmlns:tools")))
            manifest.SetAttribute("xmlns:tools", ToolsNs);

        AddFeature(doc, manifest, "android.hardware.bluetooth_le");

        AddPermission(doc, manifest, "android.permission.BLUETOOTH",           maxSdk: "30");
        AddPermission(doc, manifest, "android.permission.BLUETOOTH_ADMIN",     maxSdk: "30");
        AddPermission(doc, manifest, "android.permission.ACCESS_FINE_LOCATION", maxSdk: "30");
        AddPermission(doc, manifest, "android.permission.BLUETOOTH_SCAN",      flags: "neverForLocation");
        AddPermission(doc, manifest, "android.permission.BLUETOOTH_CONNECT");

        doc.Save(manifestPath);
        Debug.Log($"[AndroidBlePermissions] Injected BLE permissions into {manifestPath}");
    }

    void AddPermission(XmlDocument doc, XmlElement manifest, string name,
                       string maxSdk = null, string flags = null)
    {
        foreach (XmlElement e in manifest.GetElementsByTagName("uses-permission"))
            if (e.GetAttribute("name", AndroidNs) == name) return;   // already present

        var el = doc.CreateElement("uses-permission");
        el.SetAttribute("name", AndroidNs, name);
        if (maxSdk != null) el.SetAttribute("maxSdkVersion", AndroidNs, maxSdk);
        if (flags  != null) el.SetAttribute("usesPermissionFlags", AndroidNs, flags);
        manifest.AppendChild(el);
    }

    void AddFeature(XmlDocument doc, XmlElement manifest, string name)
    {
        foreach (XmlElement e in manifest.GetElementsByTagName("uses-feature"))
            if (e.GetAttribute("name", AndroidNs) == name) return;

        var el = doc.CreateElement("uses-feature");
        el.SetAttribute("name", AndroidNs, name);
        el.SetAttribute("required", AndroidNs, "false");   // don't block install on non-BLE devices
        manifest.AppendChild(el);
    }
}
#endif
