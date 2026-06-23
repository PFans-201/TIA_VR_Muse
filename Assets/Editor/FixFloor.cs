using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public class FixFloor
{
    [MenuItem("Tools/Fix Floor Physics")]
    public static void Fix()
    {
        Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/TutorialRoom.unity");
        GameObject floor = GameObject.Find("Floor");
        if (floor != null)
        {
            BoxCollider col = floor.GetComponent<BoxCollider>();
            if (col != null)
            {
                // Make the collider 10 meters thick and push it downwards
                // so no matter how messed up the Quest guardian is, they can't fall through!
                col.size = new Vector3(col.size.x, 100f, col.size.z);
                col.center = new Vector3(col.center.x, -50f, col.center.z);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log("✅ Floor thickened! You can no longer fall through it.");
            }
        }
    }
}
