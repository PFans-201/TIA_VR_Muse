using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public class FixFall
{
    [MenuItem("Tools/Fix Scene and Falling")]
    public static void Fix()
    {
        Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/TutorialRoom.unity");
        
        var rootObjs = scene.GetRootGameObjects();
        foreach (var go in rootObjs)
        {
            if (go.name.Contains("XR Origin"))
            {
                // Move player exactly to the CENTER of the room so they can't spawn outside!
                go.transform.position = new Vector3(0f, 1.5f, 0f);
            }
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        
        Debug.Log("✅ Moved XR Origin exactly to the center of the room!");
    }
}
