using UnityEngine;
using UnityEditor;

public class MuseSceneUpdater
{
    [MenuItem("Tools/Setup Muse in Tutorial Room")]
    public static void UpdateScene()
    {
        // Delete old adapters
        GameObject oldAthena = GameObject.Find("MuseAthenaAdapter");
        if (oldAthena != null) Object.DestroyImmediate(oldAthena);

        GameObject oldUdp = GameObject.Find("MuseUdpAdapter");
        if (oldUdp != null) Object.DestroyImmediate(oldUdp);

        // Create new
        GameObject museDirect = GameObject.Find("MuseDirect");
        if (museDirect == null)
        {
            museDirect = new GameObject("MuseDirect");
        }
        
        // Add components if missing
        if (museDirect.GetComponent<MuseDirectAdapter>() == null)
            museDirect.AddComponent<MuseDirectAdapter>();
            
        if (museDirect.GetComponent("VelorexeBleTransport") == null)
            museDirect.AddComponent(System.Type.GetType("VelorexeBleTransport, Assembly-CSharp"));
            
        if (museDirect.GetComponent<MuseStatusHUD>() == null)
            museDirect.AddComponent<MuseStatusHUD>();

        // Find the scene and save
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        
        Debug.Log("✅ Muse successfully added to the room! Old adapters removed.");
    }
}
