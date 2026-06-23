using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public class FixFloorWidth
{
    [MenuItem("Tools/Fix Floor Width")]
    public static void Fix()
    {
        Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/TutorialRoom.unity");
        GameObject floor = GameObject.Find("Floor");
        if (floor != null)
        {
            BoxCollider col = floor.GetComponent<BoxCollider>();
            if (col != null)
            {
                // Make the collider massive in all directions so you physically CANNOT miss it,
                // even if your Guardian boundary spawns you 50 meters away!
                col.size = new Vector3(1000f, 100f, 1000f);
                col.center = new Vector3(0f, -50f, 0f);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log("✅ Floor expanded to infinity. You will never fall again!");
            }
        }
    }
}
