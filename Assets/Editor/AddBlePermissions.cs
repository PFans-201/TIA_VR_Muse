#if UNITY_EDITOR
using System.IO;
using System.Text;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

public class AddBlePermissions : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 99;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string manifestPath = Path.Combine(path, "src/main/AndroidManifest.xml");
        if (!File.Exists(manifestPath))
        {
            Debug.LogWarning("[Muse] Could not find AndroidManifest.xml to add BLE permissions.");
            return;
        }

        XmlDocument doc = new XmlDocument();
        doc.Load(manifestPath);

        XmlElement manifestNode = doc.DocumentElement;
        if (manifestNode != null)
        {
            string androidXmlns = manifestNode.GetAttribute("xmlns:android");
            if (string.IsNullOrEmpty(androidXmlns))
                androidXmlns = "http://schemas.android.com/apk/res/android";

            // Add standard BLE permissions
            AddPermission(doc, manifestNode, androidXmlns, "android.permission.BLUETOOTH");
            AddPermission(doc, manifestNode, androidXmlns, "android.permission.BLUETOOTH_ADMIN");
            AddPermission(doc, manifestNode, androidXmlns, "android.permission.ACCESS_FINE_LOCATION");
            AddPermission(doc, manifestNode, androidXmlns, "android.permission.ACCESS_COARSE_LOCATION");
            
            // Add Android 12+ BLE permissions
            AddPermission(doc, manifestNode, androidXmlns, "android.permission.BLUETOOTH_SCAN");
            AddPermission(doc, manifestNode, androidXmlns, "android.permission.BLUETOOTH_CONNECT");

            // Add Hardware feature
            AddFeature(doc, manifestNode, androidXmlns, "android.hardware.bluetooth_le");

            doc.Save(manifestPath);
            Debug.Log("[Muse] Successfully injected Bluetooth permissions into AndroidManifest.xml!");
        }
    }

    private void AddPermission(XmlDocument doc, XmlElement manifest, string xmlns, string permission)
    {
        // Check if it already exists
        XmlNodeList nodes = manifest.GetElementsByTagName("uses-permission");
        foreach (XmlElement node in nodes)
        {
            if (node.GetAttribute("name", xmlns) == permission) return;
        }

        XmlElement newPerm = doc.CreateElement("uses-permission");
        newPerm.SetAttribute("name", xmlns, permission);
        manifest.AppendChild(newPerm);
    }
    
    private void AddFeature(XmlDocument doc, XmlElement manifest, string xmlns, string feature)
    {
        XmlNodeList nodes = manifest.GetElementsByTagName("uses-feature");
        foreach (XmlElement node in nodes)
        {
            if (node.GetAttribute("name", xmlns) == feature) return;
        }

        XmlElement newFeat = doc.CreateElement("uses-feature");
        newFeat.SetAttribute("name", xmlns, feature);
        newFeat.SetAttribute("required", xmlns, "false");
        manifest.AppendChild(newFeat);
    }
}
#endif
