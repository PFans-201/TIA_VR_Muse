using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;

/// Unity Editor tool — builds both puzzle scenes from scratch.
/// USAGE: Puzzle Game  ▶  Build All Scenes
///
/// ZenPuzzleRoom contains:
///   • Snowman puzzle  (Simple)  — 3 / 5 / 7 pieces
///   • Robot puzzle    (Complex) — 5 / 8 / 12 pieces
///   • All pieces start INACTIVE; PuzzleManager activates the right set
///   • Zen grey environment: walls 87 %, floor 82 %, table near-white
///   • Soft neutral directional light + grey ambient
///   • CognitiveLoadAdapter + PieceHintSystem for MUSE S integration
///   • Two-step DifficultyUI: puzzle type → difficulty
public static class PuzzleSceneBuilder
{
    // ── Asset paths ──────────────────────────────────────────────────────────
    private const string k_MatDir       = "Assets/Materials/PuzzleGame";
    private const string k_EntryScene   = "Assets/Scenes/EntryHall.unity";
    private const string k_TutScene     = "Assets/Scenes/TutorialRoom.unity";
    private const string k_ZenScene     = "Assets/Scenes/ZenPuzzleRoom.unity";
    private const string k_XRRigPrefab  = "Assets/VRTemplateAssets/Prefabs/Setup/Complete XR Origin Set Up Variant.prefab";

    // Redesigned 3-scene session flow (Intro → Tutorial → Game).
    private const string k_IntroScene   = "Assets/Scenes/01_Intro.unity";
    private const string k_Tut2Scene    = "Assets/Scenes/02_Tutorial.unity";
    private const string k_GameScene    = "Assets/Scenes/03_Game.unity";
    private const string k_SimPrefab    = "Assets/Samples/XR Interaction Toolkit/3.3.1/XR Interaction Simulator/XR Interaction Simulator.prefab";

    // ── Menu items ───────────────────────────────────────────────────────────

    // (internal) builds the legacy 3 scenes; kept so the puzzle template can be regenerated.
    public static void BuildAll()
    {
        EnsureFolderPath(k_MatDir);
        BuildEntryHallScene();
        BuildTutorialRoomScene();
        BuildZenPuzzleRoomScene();
        // Scene order in build: 0=EntryHall, 1=TutorialRoom, 2=ZenPuzzleRoom
        AddScenesToBuildSettings(k_EntryScene, k_TutScene, k_ZenScene);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[PuzzleSceneBuilder] All scenes built. Check File > Build Settings for scene order.");
    }

    // (internal, legacy)
    public static void BuildTutorialOnly()
    {
        EnsureFolderPath(k_MatDir);
        BuildTutorialRoomScene();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    // (internal, legacy)
    public static void BuildEntryOnly()
    {
        EnsureFolderPath(k_MatDir);
        BuildEntryHallScene();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    // (internal, legacy) the puzzle template the Game scene is generated from.
    public static void BuildZenOnly()
    {
        EnsureFolderPath(k_MatDir);
        BuildZenPuzzleRoomScene();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    // ── Scene prep for Quest (one button, applied to ALL relevant scenes) ─────

    private static readonly string[] k_AllScenes = { k_IntroScene, k_Tut2Scene, k_GameScene };

    /// ONE BUTTON. Prepares every built scene for an on-device Quest build:
    ///   • VR UI input — adds a TrackedDeviceGraphicRaycaster to each canvas and makes
    ///     the EventSystem use the XR UI Input Module (so controller rays hit the UI).
    ///   • Direct BLE — on the scenes that consume stress, swaps the EEG source to
    ///     MuseDirectAdapter + VelorexeBleTransport and disables MuseUdpAdapter.
    /// Opens, edits and saves each scene for you — no per-scene clicking.
    // (internal) the session-flow build already preps scenes; kept for manual re-prep.
    public static void PrepareScenesForQuest()
    {
        ForEachScene(k_AllScenes, () =>
        {
            int rc   = ApplyVRUIFix();
            bool ble = ApplyDirectBle();
            return $"VR-UI raycasters +{rc}; direct BLE {(ble ? "added" : "n/a")}";
        });
        Debug.Log("[PuzzleSceneBuilder] All scenes prepared for Quest. " +
                  "Switch platform to Android, then Build And Run.");
    }

    /// Just the VR UI input fix across all scenes (useful for any VR build, not only
    /// the Quest direct-BLE path).
    [MenuItem("Puzzle Game/Fix VR UI Input (all scenes)")]
    public static void FixVRUIInputAllScenes()
    {
        ForEachScene(k_AllScenes, () => $"VR-UI raycasters +{ApplyVRUIFix()}");
        Debug.Log("[PuzzleSceneBuilder] VR UI input fixed in all scenes. " +
                  "Ensure the XR rig's ray interactor has 'UI Interaction' enabled.");
    }

    /// Adds the head-locked Muse connection/stress debug HUD to every scene (on-device
    /// testing without a PC). Remove before release.
    [MenuItem("Puzzle Game/Add Muse Status HUD (all scenes)")]
    public static void AddMuseStatusHudAllScenes()
    {
        ForEachScene(k_AllScenes, () =>
        {
            if (Object.FindFirstObjectByType<MuseStatusHUD>() != null) return "already present";
            new GameObject("MuseStatusHUD").AddComponent<MuseStatusHUD>();
            return "HUD added";
        });
        Debug.Log("[PuzzleSceneBuilder] Muse status HUD added to all scenes.");
    }

    /// Adds the XR Device Simulator to every scene so you can drive the headset +
    /// controllers (and GRAB objects / click UI) with mouse + keyboard in the Editor,
    /// no headset needed. It is wrapped in EditorOnlyObject so it self-destroys in Quest
    /// builds. In Play mode: move the mouse to look; hold L/R controller keys (see the
    /// XR Device Simulator docs / on-screen hints) and click to grip/select.
    [MenuItem("Puzzle Game/Add XR Device Simulator (all scenes, Editor test)")]
    public static void AddXRDeviceSimulatorAllScenes()
    {
        // Use the fully-wired sample prefab (it carries the required Action Assets — a
        // bare XRDeviceSimulator component has none and just warns).
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(k_SimPrefab);
        if (prefab == null)
        {
            Debug.LogError("[PuzzleSceneBuilder] 'XR Interaction Simulator' sample not found at\n  " +
                           k_SimPrefab + "\nImport it via Package Manager > XR Interaction Toolkit > " +
                           "Samples > XR Interaction Simulator, then run this again.");
            return;
        }
        ForEachScene(k_AllScenes, () =>
        {
            if (Object.FindFirstObjectByType<XRInteractionSimulator>(FindObjectsInactive.Include) != null)
                return "already present";
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.AddComponent<EditorOnlyObject>();   // never ships to the Quest
            return "simulator added";
        });
        Debug.Log("[PuzzleSceneBuilder] XR Interaction Simulator added to all scenes (Editor-only). " +
                  "Enter Play mode to drive controllers + grab with mouse/keyboard.");
    }

    // ════════════════════════════════════════════════════════════════════════
    // SESSION FLOW (redesign): 3 scenes — Intro -> Tutorial -> Game
    // ════════════════════════════════════════════════════════════════════════

    /// Builds the redesigned 3-scene flow and makes it the active build (Intro = scene 0):
    ///   01_Intro    — explanation + 20 s rest baseline, then auto-advance
    ///   02_Tutorial — control practice + interaction baseline (min 40 s) + Continue
    ///   03_Game     — difficulty selection (with Muse recommendation) + puzzle
    /// The Game scene is derived from ZenPuzzleRoom, so build that first (Build All Scenes).
    [MenuItem("Puzzle Game/Build Session Flow Scenes (Intro-Tutorial-Game)")]
    public static void BuildSessionFlowScenes()
    {
        if (BlockedByPlayMode()) return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        EnsureFolderPath(k_MatDir);

        BuildIntroScene();
        BuildTutorialFlowScene();
        if (!BuildGameScene()) return;   // aborts if ZenPuzzleRoom is missing

        SetBuildSettingsScenes(k_IntroScene, k_Tut2Scene, k_GameScene);   // Intro = index 0
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[PuzzleSceneBuilder] Session flow built: 01_Intro -> 02_Tutorial -> 03_Game. " +
                  "Build Settings now start at 01_Intro. These scenes are already VR/BLE-prepped.");
    }

    private static bool BlockedByPlayMode()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[PuzzleSceneBuilder] Exit Play mode before running scene tools.");
            return true;
        }
        return false;
    }

    /// Replaces the Build Settings scene list (so index 0 is what we pass first).
    private static void SetBuildSettingsScenes(params string[] scenePaths)
    {
        var scenes = new EditorBuildSettingsScene[scenePaths.Length];
        for (int i = 0; i < scenePaths.Length; i++)
            scenes[i] = new EditorBuildSettingsScene(scenePaths[i], true);
        EditorBuildSettings.scenes = scenes;
    }

    // ── Scene 1: Intro (explanation + rest baseline) ──────────────────────────
    private static void BuildIntroScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        AddRoomLighting(8f, 8f, 3f, new Color(0.50f, 0.50f, 0.52f));
        AddFloor(Vector3.zero, 8f, 8f, GetOrCreateMat("Floor_Intro", new Color(0.82f, 0.82f, 0.82f)));
        AddWalls(8f, 8f, 3f, GetOrCreateMat("Wall_Intro", new Color(0.88f, 0.88f, 0.88f)));
        AddCeiling(8f, 8f, 3f, GetOrCreateMat("Ceiling_Intro", new Color(0.90f, 0.90f, 0.90f)));

        SpawnXRRig(new Vector3(0f, 0f, -2.5f), new Vector3(0f, 1.7f, 1.6f));

        // Persistent EEG source (connects on the first scene, holds baselines across scenes).
        var cola = new GameObject("CognitiveLoadAdapter").AddComponent<CognitiveLoadAdapter>();
        cola.hintThreshold = 0.55f;
        var eegGO  = new GameObject("MuseDirectAdapter");
        var direct = eegGO.AddComponent<MuseDirectAdapter>();
        eegGO.AddComponent<VelorexeBleTransport>();
        direct.deviceNameContains = "Muse";   // cognitiveLoad left null -> auto-targets active scene's CLA

        var intro = new GameObject("IntroController").AddComponent<IntroController>();
        intro.nextScene   = "02_Tutorial";
        intro.restSeconds = 20f;
        BuildIntroUI(intro, new Vector3(0f, 1.6f, 1.6f));

        ApplyVRUIFix();
        EditorSceneManager.SaveScene(scene, k_IntroScene);
        Debug.Log($"[PuzzleSceneBuilder] Saved {k_IntroScene}");
    }

    private static void BuildIntroUI(IntroController intro, Vector3 pos)
    {
        var root = new GameObject("IntroCanvas");
        root.transform.position = pos;
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.GetComponent<RectTransform>().sizeDelta = new Vector2(740f, 500f);
        root.transform.localScale = Vector3.one * 0.003f;
        root.AddComponent<CanvasScaler>();
        AddInteractiveRaycasters(root);

        var panel = AddUIPanel(root, "ExplanationPanel", Vector2.zero, new Vector2(740f, 500f),
                               new Color(0.92f, 0.92f, 0.92f, 0.97f));
        MakeUIText(panel.transform, "Title", "Before we start",
                   new Vector2(0f, 190f), new Vector2(680f, 70f), 40, new Color(0.18f, 0.18f, 0.18f));
        MakeUIText(panel.transform, "Body",
                   "For the next 20 seconds, please stand still and stay as relaxed as possible, " +
                   "with your eyes open.\n\nThis lets us measure your calm resting baseline so the " +
                   "game can adapt to you.\n\nWhen you understand, press OK to begin.",
                   new Vector2(0f, 0f), new Vector2(660f, 250f), 24, new Color(0.22f, 0.22f, 0.22f));
        var okay = MakeButton(panel.transform, "OkayButton", "OK", new Vector2(0f, -190f), new Vector2(220f, 56f));

        var countGO = new GameObject("Countdown");
        countGO.transform.SetParent(root.transform, false);
        var crt = countGO.AddComponent<RectTransform>();
        crt.anchoredPosition = Vector2.zero;
        crt.sizeDelta        = new Vector2(700f, 120f);
        var count = countGO.AddComponent<TextMeshProUGUI>();
        count.fontSize  = 46;
        count.alignment = TextAlignmentOptions.Center;
        count.color     = new Color(0.18f, 0.18f, 0.18f);
        countGO.SetActive(false);

        intro.explanationPanel = panel;
        intro.okayButton       = okay;
        intro.countdownLabel   = count;
    }

    // ── Scene 2: Tutorial (control practice + interaction baseline) ────────────
    private static void BuildTutorialFlowScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        AddRoomLighting(8f, 9f, 3f, new Color(0.50f, 0.50f, 0.52f));
        var tableMat = GetOrCreateMat("Table_Tutorial", new Color(0.94f, 0.94f, 0.94f));
        AddFloor(Vector3.zero, 8f, 9f, GetOrCreateMat("Floor_Tutorial", new Color(0.80f, 0.80f, 0.80f)));
        AddWalls(8f, 9f, 3f, GetOrCreateMat("Wall_Tutorial", new Color(0.88f, 0.88f, 0.88f)));
        AddCeiling(8f, 9f, 3f, GetOrCreateMat("Ceiling_Tutorial", new Color(0.90f, 0.90f, 0.90f)));
        SpawnXRRig(new Vector3(0f, 0f, -3.5f), new Vector3(0f, 1.4f, 1.8f));

        var cola = new GameObject("CognitiveLoadAdapter").AddComponent<CognitiveLoadAdapter>();
        cola.hintThreshold = 0.55f;

        // Grab practice shelf + active grab objects (the persistent MuseDirectAdapter from
        // the intro scene carries over and feeds this scene's CognitiveLoadAdapter).
        AddBox("GrabShelf", new Vector3(0f, 0.55f, 1.8f), new Vector3(1.6f, 0.08f, 0.40f), tableMat);
        var grabMats = new[]
        {
            GetOrCreateMat("GrabObj_Red",    new Color(0.85f, 0.38f, 0.38f)),
            GetOrCreateMat("GrabObj_Blue",   new Color(0.38f, 0.55f, 0.85f)),
            GetOrCreateMat("GrabObj_Yellow", new Color(0.90f, 0.82f, 0.30f)),
            GetOrCreateMat("GrabObj_Green",  new Color(0.38f, 0.75f, 0.45f)),
        };
        float gx = -0.55f;
        foreach (var mat in grabMats)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "GrabSphere";
            go.transform.position   = new Vector3(gx, 0.87f, 1.8f);
            go.transform.localScale = Vector3.one * 0.14f;
            go.GetComponent<Renderer>().material = mat;
            go.AddComponent<Rigidbody>().mass = 0.15f;
            go.AddComponent<XRGrabInteractable>();
            gx += 0.37f;
        }
        AddBox("Pedestal_Left",  new Vector3(-0.7f, 0.55f, 0.5f), new Vector3(0.30f, 1.1f, 0.30f), tableMat);
        AddBox("Pedestal_Right", new Vector3( 0.7f, 0.55f, 0.5f), new Vector3(0.30f, 1.1f, 0.30f), tableMat);

        var tut = new GameObject("TutorialController").AddComponent<TutorialController>();
        tut.nextScene  = "03_Game";
        tut.minSeconds = 40f;
        BuildTutorialFlowUI(tut, new Vector3(0f, 1.8f, 2.8f));

        ApplyVRUIFix();
        EditorSceneManager.SaveScene(scene, k_Tut2Scene);
        Debug.Log($"[PuzzleSceneBuilder] Saved {k_Tut2Scene}");
    }

    private static void BuildTutorialFlowUI(TutorialController tut, Vector3 pos)
    {
        var root = new GameObject("TutorialCanvas");
        root.transform.position = pos;
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.GetComponent<RectTransform>().sizeDelta = new Vector2(740f, 560f);
        root.transform.localScale = Vector3.one * 0.003f;
        root.AddComponent<CanvasScaler>();
        AddInteractiveRaycasters(root);

        var info = AddUIPanel(root, "InfoPanel", new Vector2(0f, 70f), new Vector2(740f, 380f),
                              new Color(0.92f, 0.92f, 0.92f, 0.97f));
        MakeUIText(info.transform, "Title", "Get used to VR",
                   new Vector2(0f, 140f), new Vector2(680f, 60f), 38, new Color(0.18f, 0.18f, 0.18f));
        MakeUIText(info.transform, "Body",
                   "Use the thumbstick to move and turn.\n\nReach toward an object on the shelf and " +
                   "squeeze the grip button to pick it up; release to drop it on a pedestal.\n\n" +
                   "Take your time getting comfortable.",
                   new Vector2(0f, -20f), new Vector2(660f, 240f), 24, new Color(0.22f, 0.22f, 0.22f));

        var timer = MakeUIText(root.transform, "Timer", "",
                               new Vector2(0f, -150f), new Vector2(700f, 50f), 26, new Color(0.20f, 0.20f, 0.20f));

        var proceed = AddUIPanel(root, "ProceedPanel", new Vector2(0f, -210f), new Vector2(740f, 150f),
                                 new Color(0.88f, 0.92f, 0.88f, 0.97f));
        MakeUIText(proceed.transform, "ProceedText",
                   "You can continue now, or stay longer to get comfortable.",
                   new Vector2(0f, 35f), new Vector2(680f, 60f), 24, new Color(0.20f, 0.20f, 0.20f));
        var cont = MakeButton(proceed.transform, "ContinueButton", "Continue",
                              new Vector2(0f, -35f), new Vector2(240f, 54f));
        proceed.SetActive(false);

        tut.infoPanels     = info;
        tut.proceedPanel   = proceed;
        tut.continueButton = cont;
        tut.timerLabel     = timer;
    }

    // ── Scene 3: Game (difficulty selection + puzzle, derived from ZenPuzzleRoom) ──
    private static bool BuildGameScene()
    {
        // Always rebuild the puzzle template fresh so the game scene inherits the
        // CURRENT, self-consistent layout — XR rig facing the puzzle table with the
        // difficulty UI in front. (Reusing a stale ZenPuzzleRoom left the rig
        // mis-oriented at 130°, so the player spawned facing away from the puzzle.)
        BuildZenPuzzleRoomScene();
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(k_ZenScene) == null)
        {
            Debug.LogError("[PuzzleSceneBuilder] Could not generate the puzzle template (ZenPuzzleRoom).");
            return false;
        }
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(k_GameScene) != null)
            AssetDatabase.DeleteAsset(k_GameScene);
        AssetDatabase.CopyAsset(k_ZenScene, k_GameScene);
        AssetDatabase.Refresh();

        var scene = EditorSceneManager.OpenScene(k_GameScene, OpenSceneMode.Single);
        ApplyDirectBle();   // ensure on-device BLE source (idempotent)
        ApplyVRUIFix();     // ensure VR interaction
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        // ZenPuzzleRoom is only an intermediate template for the session flow — remove
        // it so it can't linger in the project and later be copied in a stale state.
        AssetDatabase.DeleteAsset(k_ZenScene);
        Debug.Log($"[PuzzleSceneBuilder] Saved {k_GameScene} (built fresh; template removed).");
        return true;
    }

    /// World-space button: Image + Button + centred TMP label. Returns the Button.
    private static Button MakeButton(Transform parent, string name, string label,
                                     Vector2 anchoredPos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta        = size;
        go.AddComponent<UnityEngine.UI.Image>().color = new Color(0.60f, 0.78f, 0.60f);
        var btn = go.AddComponent<Button>();

        var lblGO = new GameObject("Label");
        lblGO.transform.SetParent(go.transform, false);
        lblGO.AddComponent<RectTransform>().sizeDelta = size - new Vector2(16f, 8f);
        var lbl = lblGO.AddComponent<TextMeshProUGUI>();
        lbl.text      = label;
        lbl.fontSize  = 26;
        lbl.alignment = TextAlignmentOptions.Center;
        lbl.color     = Color.white;
        return btn;
    }

    // ── open-scene operations (no save; ForEachScene handles open/save) ────────

    /// Adds a TrackedDeviceGraphicRaycaster to every root canvas in the open scene and
    /// ensures the EventSystem runs the XR UI Input Module. Returns canvases touched.
    private static int ApplyVRUIFix()
    {
        int added = 0;
        foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!canvas.isRootCanvas) continue;
            if (canvas.GetComponent<TrackedDeviceGraphicRaycaster>() == null)
            {
                canvas.gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
                added++;
            }
        }
        var es = Object.FindFirstObjectByType<EventSystem>(FindObjectsInactive.Include);
        if (es == null) es = new GameObject("EventSystem").AddComponent<EventSystem>();
        if (es.GetComponent<XRUIInputModule>() == null)
        {
            foreach (var m in es.GetComponents<BaseInputModule>()) Object.DestroyImmediate(m);
            es.gameObject.AddComponent<XRUIInputModule>();
        }

        // ANY XR interaction (UI rays AND grab objects) needs an XRInteractionManager in
        // the scene. The rig prefab usually carries one, but ensure it so grab/UI never
        // silently fails for want of a manager.
        if (Object.FindFirstObjectByType<XRInteractionManager>(FindObjectsInactive.Include) == null)
            new GameObject("XR Interaction Manager").AddComponent<XRInteractionManager>();

        return added;
    }

    /// Swaps the open scene's EEG source to on-device direct BLE. Returns true if it
    /// added the adapter (scene has a CognitiveLoadAdapter and none was present yet).
    private static bool ApplyDirectBle()
    {
        var cola = Object.FindFirstObjectByType<CognitiveLoadAdapter>();
        if (cola == null) return false;                                          // scene doesn't consume stress
        if (Object.FindFirstObjectByType<MuseDirectAdapter>() != null) return false;  // already done

        var udp = Object.FindFirstObjectByType<MuseUdpAdapter>();
        if (udp != null) udp.enabled = false;                                    // don't double-drive stress

        var go     = new GameObject("MuseDirectAdapter");
        var direct = go.AddComponent<MuseDirectAdapter>();
        go.AddComponent<VelorexeBleTransport>();
        direct.cognitiveLoad      = cola;
        direct.deviceNameContains = "Muse";
        return true;
    }

    /// Opens each scene in turn, runs <paramref name="op"/>, saves it, then restores
    /// the scene that was open before. Skips missing scene files.
    private static void ForEachScene(string[] scenePaths, System.Func<string> op)
    {
        if (BlockedByPlayMode()) return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        string original = SceneManager.GetActiveScene().path;
        foreach (var path in scenePaths)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
            {
                Debug.LogWarning($"[PuzzleSceneBuilder] Scene not found, skipping: {path}");
                continue;
            }
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            string result = op();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[PuzzleSceneBuilder]   {System.IO.Path.GetFileNameWithoutExtension(path)}: {result}");
        }
        if (!string.IsNullOrEmpty(original))
            EditorSceneManager.OpenScene(original, OpenSceneMode.Single);
    }

    // ════════════════════════════════════════════════════════════════════════
    // SCENE: Entry Hall  (calm, minimal, grey)
    // ════════════════════════════════════════════════════════════════════════

    private static void BuildEntryHallScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Soft neutral light
        AddDirectionalLight(new Color(0.96f, 0.94f, 0.90f), 0.60f, Quaternion.Euler(50f, -20f, 0f));
        SetAmbientFlat(new Color(0.30f, 0.30f, 0.30f));

        // Zen grey materials
        var floorMat  = GetOrCreateMat("Floor_Entry",  new Color(0.78f, 0.78f, 0.78f));
        var wallMat   = GetOrCreateMat("Wall_Entry",   new Color(0.84f, 0.84f, 0.84f));
        var portalMat = GetOrCreateMat("Portal_Frame", new Color(0.72f, 0.72f, 0.72f));

        // 6 × 8 × 3 m room
        AddFloor(Vector3.zero, 6f, 8f, floorMat);
        AddWalls(6f, 8f, 3f, wallMat);

        // Subtle grey archway
        AddBox("Portal_PostLeft",  new Vector3(-1.0f, 1.5f, 3.8f), new Vector3(0.18f, 3.0f, 0.18f), portalMat);
        AddBox("Portal_PostRight", new Vector3( 1.0f, 1.5f, 3.8f), new Vector3(0.18f, 3.0f, 0.18f), portalMat);
        AddBox("Portal_TopBeam",   new Vector3( 0.0f, 3.1f, 3.8f), new Vector3(2.20f, 0.18f, 0.18f), portalMat);

        // Portal trigger
        var portalGO = AddBox("ScenePortal", new Vector3(0f, 1.5f, 4.0f), new Vector3(1.8f, 3.0f, 0.4f), null);
        portalGO.GetComponent<Renderer>().enabled = false;
        var col    = portalGO.GetComponent<BoxCollider>();
        col.isTrigger = true;
        var portal = portalGO.AddComponent<ScenePortal>();
        portal.targetSceneName = "ZenPuzzleRoom";
        portal.transitionDelay = 0.5f;

        SpawnXRRig(new Vector3(0f, 0f, -2f), new Vector3(0f, 1.6f, 1.0f));   // face the entrance sign / content
        AddWorldText("EntranceSign", new Vector3(0f, 2.2f, 1.0f),
                     "Walk through the archway\nto begin the puzzle");

        EditorSceneManager.SaveScene(scene, k_EntryScene);
        Debug.Log($"[PuzzleSceneBuilder] Saved {k_EntryScene}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // SCENE: Tutorial Room  (VR onboarding + EEG baseline calibration)
    // ════════════════════════════════════════════════════════════════════════

    private static void BuildTutorialRoomScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Warm, slightly brighter grey to feel welcoming and distinct from the puzzle room
        AddDirectionalLight(new Color(0.97f, 0.96f, 0.93f), 0.70f, Quaternion.Euler(45f, -30f, 0f));
        SetAmbientFlat(new Color(0.38f, 0.38f, 0.38f));

        var floorMat  = GetOrCreateMat("Floor_Tutorial",  new Color(0.80f, 0.80f, 0.80f));
        var wallMat   = GetOrCreateMat("Wall_Tutorial",   new Color(0.88f, 0.88f, 0.88f));
        var tableMat  = GetOrCreateMat("Table_Tutorial",  new Color(0.94f, 0.94f, 0.94f));
        var restMat   = GetOrCreateMat("RestZone",        new Color(0.70f, 0.85f, 0.70f));   // soft green circle
        var grabMat0  = GetOrCreateMat("GrabObj_Red",     new Color(0.85f, 0.38f, 0.38f));
        var grabMat1  = GetOrCreateMat("GrabObj_Blue",    new Color(0.38f, 0.55f, 0.85f));
        var grabMat2  = GetOrCreateMat("GrabObj_Yellow",  new Color(0.90f, 0.82f, 0.30f));
        var grabMat3  = GetOrCreateMat("GrabObj_Green",   new Color(0.38f, 0.75f, 0.45f));
        var pedestalM = GetOrCreateMat("Pedestal",        new Color(0.75f, 0.75f, 0.75f));

        // 8 × 9 × 3 m room (slightly longer than puzzle room)
        AddFloor(Vector3.zero, 8f, 9f, floorMat);
        AddWalls(8f, 9f, 3f, wallMat);

        SpawnXRRig(new Vector3(0f, 0f, -3.5f), new Vector3(0f, 1.8f, 2.8f));   // face the tutorial panel

        // ── Rest zone — soft green disc on floor where participant stands during baseline ──
        var restZone = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        restZone.name = "RestZoneMarker";
        restZone.transform.position   = new Vector3(0f, 0.01f, -1.0f);
        restZone.transform.localScale = new Vector3(1.0f, 0.01f, 1.0f);
        restZone.GetComponent<Renderer>().material = restMat;
        Object.DestroyImmediate(restZone.GetComponent<Collider>());
        restZone.SetActive(false);   // TutorialManager enables it during rest-baseline phase

        // ── Grab practice shelf ───────────────────────────────────────────────
        AddBox("GrabShelf",  new Vector3(0f, 0.55f, 1.8f), new Vector3(1.6f, 0.08f, 0.40f), tableMat);

        // Four grab objects sitting on the shelf — start INACTIVE (enabled by TutorialManager)
        var grabDefs = new (string name, Vector3 pos, Material mat)[]
        {
            ("GrabSphere_Red",    new Vector3(-0.55f, 0.87f, 1.8f), grabMat0),
            ("GrabSphere_Blue",   new Vector3(-0.18f, 0.87f, 1.8f), grabMat1),
            ("GrabSphere_Yellow", new Vector3( 0.18f, 0.87f, 1.8f), grabMat2),
            ("GrabSphere_Green",  new Vector3( 0.55f, 0.87f, 1.8f), grabMat3),
        };
        var grabObjects = new List<GameObject>();
        foreach (var d in grabDefs)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = d.name;
            go.transform.position   = d.pos;
            go.transform.localScale = Vector3.one * 0.14f;
            go.GetComponent<Renderer>().material = d.mat;

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.15f;

            go.AddComponent<XRGrabInteractable>();
            go.SetActive(false);   // TutorialManager enables during VRTutorial phase
            grabObjects.Add(go);
        }

        // ── Drop pedestals ────────────────────────────────────────────────────
        AddBox("Pedestal_Left",  new Vector3(-0.7f, 0.55f, 0.5f), new Vector3(0.30f, 1.1f, 0.30f), pedestalM);
        AddBox("Pedestal_Right", new Vector3( 0.7f, 0.55f, 0.5f), new Vector3(0.30f, 1.1f, 0.30f), pedestalM);

        // ── Systems ───────────────────────────────────────────────────────────
        var colaGO = new GameObject("CognitiveLoadAdapter");
        var cola   = colaGO.AddComponent<CognitiveLoadAdapter>();
        cola.hintThreshold = 0.55f;

        var museGO = new GameObject("MuseAthenaAdapter");
        var muse   = museGO.AddComponent<MuseAthenaAdapter>();
        muse.cognitiveLoad         = cola;
        muse.updateIntervalSeconds = 2f;
        muse.windowSeconds         = 4f;
        muse.enableOptical         = true;
        // baselineDurationSeconds = 60 (adapter's own auto-baseline still runs as fallback
        // if tutorial baseline collection fails — TutorialManager overrides it on completion)

        // UDP bridge source — on Linux BrainFlow can't stream the Athena, so the
        // Python bridge (Tools/muse_bridge.py) feeds stress in over UDP. Persists
        // across scene loads and pushes into whichever CognitiveLoadAdapter is active.
        var udpGO = new GameObject("MuseUdpAdapter");
        var udp   = udpGO.AddComponent<MuseUdpAdapter>();
        udp.cognitiveLoad = cola;
        udp.port          = 5005;   // bridge -> Unity (stress)
        udp.controlPort   = 5006;   // Unity -> bridge (baseline commands, --unity mode)

        // ── Tutorial Manager ──────────────────────────────────────────────────
        var tmGO = new GameObject("TutorialManager");
        var tm   = tmGO.AddComponent<TutorialManager>();
        tm.museAdapter            = muse;
        tm.udpAdapter             = udp;
        tm.puzzleSceneName        = "ZenPuzzleRoom";
        tm.restBaselineDuration   = 60f;
        tm.activeBaselineDuration = 60f;
        tm.restZoneMarker         = restZone;
        tm.tutorialGrabObjects.AddRange(grabObjects);

        // ── Instruction Canvas ────────────────────────────────────────────────
        BuildTutorialUI(tm, new Vector3(0f, 1.8f, 2.8f));

        EditorSceneManager.SaveScene(scene, k_TutScene);
        Debug.Log($"[PuzzleSceneBuilder] Saved {k_TutScene}");
    }

    /// Builds the world-space instruction canvas and wires it to TutorialManager.
    private static void BuildTutorialUI(TutorialManager tm, Vector3 pos)
    {
        var root = new GameObject("TutorialCanvas");
        root.transform.position = pos;
        root.transform.rotation = Quaternion.identity;

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = root.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(700f, 520f);
        root.transform.localScale = Vector3.one * 0.003f;
        root.AddComponent<CanvasScaler>();
        AddInteractiveRaycasters(root);

        // Background panel
        var bg = AddUIPanel(root, "Background", Vector2.zero, new Vector2(700f, 520f),
                            new Color(0.92f, 0.92f, 0.92f, 0.96f));

        // Title
        var titleGO  = new GameObject("InstructionTitle");
        titleGO.transform.SetParent(bg.transform, false);
        var titleRT  = titleGO.AddComponent<RectTransform>();
        titleRT.anchoredPosition = new Vector2(0f, 190f);
        titleRT.sizeDelta        = new Vector2(660f, 70f);
        var titleTMP = titleGO.AddComponent<TextMeshProUGUI>();
        titleTMP.text      = "Welcome";
        titleTMP.fontSize  = 42f;
        titleTMP.alignment = TextAlignmentOptions.Center;
        titleTMP.color     = new Color(0.18f, 0.18f, 0.18f);

        // Body
        var bodyGO  = new GameObject("InstructionBody");
        bodyGO.transform.SetParent(bg.transform, false);
        var bodyRT  = bodyGO.AddComponent<RectTransform>();
        bodyRT.anchoredPosition = new Vector2(0f, 20f);
        bodyRT.sizeDelta        = new Vector2(640f, 280f);
        var bodyTMP = bodyGO.AddComponent<TextMeshProUGUI>();
        bodyTMP.text      = "";
        bodyTMP.fontSize  = 24f;
        bodyTMP.alignment = TextAlignmentOptions.Center;
        bodyTMP.color     = new Color(0.22f, 0.22f, 0.22f);

        // Progress bar
        var sliderGO = new GameObject("ProgressBar");
        sliderGO.transform.SetParent(bg.transform, false);
        var sliderRT = sliderGO.AddComponent<RectTransform>();
        sliderRT.anchoredPosition = new Vector2(0f, -185f);
        sliderRT.sizeDelta        = new Vector2(580f, 22f);
        var slider = sliderGO.AddComponent<Slider>();
        slider.minValue = 0f; slider.maxValue = 1f; slider.value = 0f;

        // Continue button
        var btnGO  = new GameObject("ContinueButton");
        btnGO.transform.SetParent(bg.transform, false);
        var btnRT  = btnGO.AddComponent<RectTransform>();
        btnRT.anchoredPosition = new Vector2(0f, -215f);
        btnRT.sizeDelta        = new Vector2(260f, 52f);
        btnGO.AddComponent<UnityEngine.UI.Image>().color = new Color(0.60f, 0.78f, 0.60f);
        var btn    = btnGO.AddComponent<Button>();
        var lblGO  = new GameObject("Label");
        lblGO.transform.SetParent(btnGO.transform, false);
        var lblRT  = lblGO.AddComponent<RectTransform>();
        lblRT.sizeDelta = new Vector2(240f, 48f);
        var lbl    = lblGO.AddComponent<TextMeshProUGUI>();
        lbl.text      = "Continue";
        lbl.fontSize  = 26f;
        lbl.alignment = TextAlignmentOptions.Center;
        lbl.color     = Color.white;

        // Wire references into TutorialManager
        tm.instructionPanel = root;
        tm.instructionTitle = titleTMP;
        tm.instructionBody  = bodyTMP;
        tm.progressBar      = slider;
        tm.continueButton   = btn;
        tm.continueLabel    = lbl;
    }

    private static GameObject AddUIPanel(GameObject parent, string name,
                                          Vector2 anchoredPos, Vector2 size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta        = size;
        go.AddComponent<UnityEngine.UI.Image>().color = color;
        return go;
    }

    // ════════════════════════════════════════════════════════════════════════
    // SCENE: Zen Puzzle Room
    // ════════════════════════════════════════════════════════════════════════

    private static void BuildZenPuzzleRoomScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Bright, even lighting — returned so HardModeDarkroom can switch it all off on Hard.
        var litAmbient = new Color(0.50f, 0.50f, 0.52f);
        var roomLights = AddRoomLighting(8f, 8f, 3f, litAmbient);

        // Zen grey materials
        var floorMat = GetOrCreateMat("Floor_Zen",  new Color(0.82f, 0.82f, 0.82f));
        var wallMat  = GetOrCreateMat("Wall_Zen",   new Color(0.87f, 0.87f, 0.87f));
        var tableMat = GetOrCreateMat("Table_Zen",  new Color(0.93f, 0.93f, 0.93f));

        // 8 × 8 × 3 m room (roofed so it feels enclosed and contains the pieces)
        AddFloor(Vector3.zero, 8f, 8f, floorMat);
        AddWalls(8f, 8f, 3f, wallMat);
        AddCeiling(8f, 8f, 3f, GetOrCreateMat("Ceiling_Zen", new Color(0.90f, 0.90f, 0.90f)));

        // Central puzzle table  (top surface at y = 1.0)
        AddBox("PuzzleTable", new Vector3(0f, 0.5f, 0f), new Vector3(1.4f, 1.0f, 1.4f), tableMat);

        SpawnXRRig(new Vector3(0f, 0f, -3f), new Vector3(0f, 1.0f, 0f));   // face the puzzle table

        // ── Systems ───────────────────────────────────────────────────────
        var colaGO = new GameObject("CognitiveLoadAdapter");
        var cola   = colaGO.AddComponent<CognitiveLoadAdapter>();
        cola.hintThreshold = 0.55f;   // colour hints appear at moderate stress

        // Muse S Athena BrainFlow adapter — feeds SetStressLevel() from real EEG.
        // Set macAddress in the Inspector before entering Play Mode.
        // While the device is absent, use CognitiveLoadAdapter's debugStressOverride slider.
        var museGO  = new GameObject("MuseAthenaAdapter");
        var muse    = museGO.AddComponent<MuseAthenaAdapter>();
        muse.cognitiveLoad         = cola;
        muse.updateIntervalSeconds = 2f;
        muse.windowSeconds         = 4f;
        muse.enableOptical         = true;

        var phsGO = new GameObject("PieceHintSystem");
        var phs   = phsGO.AddComponent<PieceHintSystem>();

        // SustainedStressDetector gates mechanical assistance on temporally persistent,
        // low-variance stress — preventing transient noise spikes from triggering help.
        var ssdGO = new GameObject("SustainedStressDetector");
        var ssd   = ssdGO.AddComponent<SustainedStressDetector>();
        ssd.cognitiveLoad      = cola;
        ssd.onsetThreshold     = 0.60f;   // rolling mean must exceed this
        ssd.maxVarianceForOnset = 0.04f;  // signal must be stable (not noisy)
        ssd.minOnsetSeconds    = 20f;     // must be elevated for 20 s continuously
        ssd.recoveryThreshold  = 0.40f;   // hysteresis: drop below this to exit Stressed
        ssd.minRecoverySeconds = 15f;
        ssd.windowSeconds      = 30f;

        var adcGO = new GameObject("AdaptiveDifficultyController");
        var adc   = adcGO.AddComponent<AdaptiveDifficultyController>();
        adc.assistanceOnset  = 0.70f;
        adc.assistanceMax    = 0.95f;
        adc.rampUpSpeed      = 1.5f;
        adc.rampDownSpeed    = 0.35f;
        adc.cognitiveLoad    = cola;
        adc.stressDetector   = ssd;       // gate assistance on sustained stress
        // adc.puzzleManager wired after pm is created (see below)

        // ── Puzzle Manager ────────────────────────────────────────────────
        var pmGO = new GameObject("PuzzleManager");
        var pm   = pmGO.AddComponent<PuzzleManager>();
        pm.hintSystem   = phs;
        adc.puzzleManager = pm;   // wire adaptive controller → manager

        var anchorGO = new GameObject("PuzzleAnchor");
        anchorGO.transform.position = new Vector3(0f, 1.01f, 0f);
        pm.puzzleAnchor = anchorGO.transform;

        // ── Snowman puzzle ────────────────────────────────────────────────
        //   (n, primitive, world pos, euler, scale, colour)
        var snowmanDefs = new (string n, PrimitiveType p, Vector3 pos, Vector3 euler, Vector3 scale, Color col)[]
        {
            // Easy (3)
            ("Body",      PrimitiveType.Sphere,   new Vector3( 0.00f, 1.35f, 0f), Vector3.zero,           new Vector3(0.40f, 0.50f, 0.40f), new Color(0.92f, 0.92f, 0.92f)),
            ("Head",      PrimitiveType.Sphere,   new Vector3( 0.00f, 1.75f, 0f), Vector3.zero,           new Vector3(0.30f, 0.30f, 0.30f), new Color(0.94f, 0.94f, 0.92f)),
            ("Hat",       PrimitiveType.Cylinder, new Vector3( 0.00f, 2.00f, 0f), Vector3.zero,           new Vector3(0.25f, 0.12f, 0.25f), new Color(0.14f, 0.10f, 0.06f)),
            // Medium adds arms (5)
            ("LeftArm",   PrimitiveType.Cylinder, new Vector3(-0.45f, 1.45f, 0f), new Vector3(0f,  0f,  90f), new Vector3(0.09f, 0.28f, 0.09f), new Color(0.75f, 0.68f, 0.55f)),
            ("RightArm",  PrimitiveType.Cylinder, new Vector3( 0.45f, 1.45f, 0f), new Vector3(0f,  0f, -90f), new Vector3(0.09f, 0.28f, 0.09f), new Color(0.75f, 0.68f, 0.55f)),
            // Hard adds legs (7)
            ("LeftLeg",   PrimitiveType.Cylinder, new Vector3(-0.14f, 1.02f, 0f), new Vector3(0f,  0f,  10f), new Vector3(0.12f, 0.28f, 0.12f), new Color(0.88f, 0.88f, 0.86f)),
            ("RightLeg",  PrimitiveType.Cylinder, new Vector3( 0.14f, 1.02f, 0f), new Vector3(0f,  0f, -10f), new Vector3(0.12f, 0.28f, 0.12f), new Color(0.88f, 0.88f, 0.86f)),
        };

        var snapRootSnowman = new GameObject("SnapZones_Snowman");
        BuildPuzzleSet("Snowman", snowmanDefs, snapRootSnowman.transform, pm, isSnowman: true);

        // ── Robot puzzle ──────────────────────────────────────────────────
        //   Assembled on the same anchor (only one set active at a time)
        var robotDefs = new (string n, PrimitiveType p, Vector3 pos, Vector3 euler, Vector3 scale, Color col)[]
        {
            // Easy (5): Head, Torso, LeftArm, RightArm, LeftLeg
            ("Head",         PrimitiveType.Cube,     new Vector3( 0.00f, 1.90f,  0.00f), Vector3.zero,             new Vector3(0.26f, 0.26f, 0.22f), new Color(0.60f, 0.72f, 0.85f)),
            ("Torso",        PrimitiveType.Cube,     new Vector3( 0.00f, 1.52f,  0.00f), Vector3.zero,             new Vector3(0.38f, 0.36f, 0.22f), new Color(0.55f, 0.65f, 0.75f)),
            ("LeftArm",      PrimitiveType.Cylinder, new Vector3(-0.32f, 1.58f,  0.00f), new Vector3(0f, 0f,  80f), new Vector3(0.09f, 0.25f, 0.09f), new Color(0.62f, 0.68f, 0.72f)),
            ("RightArm",     PrimitiveType.Cylinder, new Vector3( 0.32f, 1.58f,  0.00f), new Vector3(0f, 0f, -80f), new Vector3(0.09f, 0.25f, 0.09f), new Color(0.62f, 0.68f, 0.72f)),
            ("LeftLeg",      PrimitiveType.Cylinder, new Vector3(-0.12f, 1.20f,  0.00f), Vector3.zero,             new Vector3(0.10f, 0.28f, 0.10f), new Color(0.50f, 0.52f, 0.55f)),
            // Medium adds (8): + RightLeg, LeftForearm, RightForearm
            ("RightLeg",     PrimitiveType.Cylinder, new Vector3( 0.12f, 1.20f,  0.00f), Vector3.zero,             new Vector3(0.10f, 0.28f, 0.10f), new Color(0.50f, 0.52f, 0.55f)),
            ("LeftForearm",  PrimitiveType.Cylinder, new Vector3(-0.50f, 1.38f,  0.00f), new Vector3(0f, 0f,  65f), new Vector3(0.07f, 0.20f, 0.07f), new Color(0.58f, 0.62f, 0.65f)),
            ("RightForearm", PrimitiveType.Cylinder, new Vector3( 0.50f, 1.38f,  0.00f), new Vector3(0f, 0f, -65f), new Vector3(0.07f, 0.20f, 0.07f), new Color(0.58f, 0.62f, 0.65f)),
            // Hard adds (12): + LeftFoot, RightFoot, LeftEye, RightEye
            ("LeftFoot",     PrimitiveType.Cube,     new Vector3(-0.12f, 0.97f,  0.06f), Vector3.zero,             new Vector3(0.16f, 0.07f, 0.24f), new Color(0.42f, 0.44f, 0.46f)),
            ("RightFoot",    PrimitiveType.Cube,     new Vector3( 0.12f, 0.97f,  0.06f), Vector3.zero,             new Vector3(0.16f, 0.07f, 0.24f), new Color(0.42f, 0.44f, 0.46f)),
            ("LeftEye",      PrimitiveType.Sphere,   new Vector3(-0.07f, 1.96f,  0.12f), Vector3.zero,             new Vector3(0.055f,0.055f,0.055f), new Color(0.08f, 0.08f, 0.10f)),
            ("RightEye",     PrimitiveType.Sphere,   new Vector3( 0.07f, 1.96f,  0.12f), Vector3.zero,             new Vector3(0.055f,0.055f,0.055f), new Color(0.08f, 0.08f, 0.10f)),
        };

        var snapRootRobot = new GameObject("SnapZones_Robot");
        BuildPuzzleSet("Robot", robotDefs, snapRootRobot.transform, pm, isSnowman: false);

        // ── Two-step Difficulty UI ────────────────────────────────────────
        BuildDifficultyCanvas(pm, new Vector3(0f, 1.8f, -1.8f));

        // ── Ambient instruction text ──────────────────────────────────────
        AddWorldText("RoomLabel", new Vector3(0f, 2.85f, -3.8f), "Assemble the puzzle");

        // ── Keep pieces inside the room (backstop for grabbed pieces) ──────
        var contGO = new GameObject("RoomPieceContainer");
        var cont   = contGO.AddComponent<RoomPieceContainer>();
        cont.interiorCenter = new Vector3(0f, 1.4f, 0f);
        cont.interiorSize   = new Vector3(7.4f, 2.7f, 7.4f);

        // ── Hard mode: dark room + grabbable forearm lantern ──────────────
        var lantern = BuildLantern(new Vector3(0f, 1.2f, -1.5f));   // floats in front of the player, reachable
        var darkGO  = new GameObject("HardModeDarkroom");
        var dark    = darkGO.AddComponent<HardModeDarkroom>();
        dark.roomLights    = roomLights.ToArray();
        dark.lantern       = lantern;
        dark.puzzleManager = pm;
        dark.litAmbient    = litAmbient;

        EditorSceneManager.SaveScene(scene, k_ZenScene);
        Debug.Log($"[PuzzleSceneBuilder] Saved {k_ZenScene}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Puzzle set builder — shared by both puzzle types
    // ════════════════════════════════════════════════════════════════════════

    private static void BuildPuzzleSet(
        string prefix,
        (string n, PrimitiveType p, Vector3 pos, Vector3 euler, Vector3 scale, Color col)[] defs,
        Transform snapRoot,
        PuzzleManager pm,
        bool isSnowman)
    {
        var allPieces = new List<GameObject>();

        foreach (var d in defs)
        {
            var (pieceGO, _) = MakePuzzlePiece($"{prefix}_{d.n}", d.p, d.pos, d.euler, d.scale, d.col, snapRoot);
            pieceGO.SetActive(false);   // inactive until player selects this puzzle
            allPieces.Add(pieceGO);
        }

        if (isSnowman)
        {
            pm.snowmanHardPieces   = new List<GameObject>(allPieces);          // all 7
            pm.snowmanMediumPieces = allPieces.GetRange(0, 5);                 // first 5
            pm.snowmanEasyPieces   = allPieces.GetRange(0, 3);                 // first 3
        }
        else
        {
            pm.robotHardPieces   = new List<GameObject>(allPieces);            // all 12
            pm.robotMediumPieces = allPieces.GetRange(0, 8);                   // first 8
            pm.robotEasyPieces   = allPieces.GetRange(0, 5);                   // first 5
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Puzzle piece + snap zone factory
    // ════════════════════════════════════════════════════════════════════════

    private static (GameObject piece, GameObject snapZone) MakePuzzlePiece(
        string pieceName, PrimitiveType prim,
        Vector3 solvedPos, Vector3 solvedEuler, Vector3 scale,
        Color pieceColor, Transform snapRoot)
    {
        // ── Piece ─────────────────────────────────────────────────────────
        var go = GameObject.CreatePrimitive(prim);
        go.name = $"Piece_{pieceName}";
        go.transform.position   = solvedPos;
        go.transform.rotation   = Quaternion.Euler(solvedEuler);
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().material = GetOrCreateMat($"Piece_{pieceName}", pieceColor);

        var rb = go.AddComponent<Rigidbody>();
        rb.mass          = 0.3f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        go.AddComponent<XRGrabInteractable>();

        var pp = go.AddComponent<PuzzlePiece>();
        pp.solveThreshold = 0.05f;
        pp.solvedMaterial = GetOrCreateMat("Piece_Solved", new Color(0.98f, 0.94f, 0.80f));

        // ── Snap zone ─────────────────────────────────────────────────────
        var snapGO = new GameObject($"SnapZone_{pieceName}");
        snapGO.transform.SetParent(snapRoot, worldPositionStays: true);
        snapGO.transform.position = solvedPos;
        snapGO.transform.rotation = Quaternion.Euler(solvedEuler);
        snapGO.SetActive(false);    // hidden until puzzle starts

        // Ghost mesh — semi-transparent copy of the piece geometry
        var ghost = GameObject.CreatePrimitive(prim);
        ghost.name = "Ghost";
        ghost.transform.SetParent(snapGO.transform, worldPositionStays: false);
        ghost.transform.localPosition = Vector3.zero;
        ghost.transform.localRotation = Quaternion.identity;
        ghost.transform.localScale    = scale;
        Object.DestroyImmediate(ghost.GetComponent<Collider>());

        var ghostRend = ghost.GetComponent<Renderer>();
        ghostRend.shadowCastingMode = ShadowCastingMode.Off;
        ghostRend.receiveShadows    = false;
        ghostRend.material = GetOrCreateMat("Ghost_Idle", new Color(0.68f, 0.82f, 1.00f, 0.20f), transparent: true);

        var msz = snapGO.AddComponent<MagneticSnapZone>();
        msz.linkedPiece         = pp;
        msz.ghostRenderer       = ghostRend;
        msz.ghostIdleMaterial   = GetOrCreateMat("Ghost_Idle",   new Color(0.68f, 0.82f, 1.00f, 0.20f), transparent: true);
        msz.ghostActiveMaterial = GetOrCreateMat("Ghost_Active", new Color(0.48f, 0.88f, 1.00f, 0.45f), transparent: true);
        msz.activationRange     = 0.15f;

        pp.correctPlacementTarget = snapGO.transform;

        return (go, snapGO);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Two-step Difficulty UI canvas
    // ════════════════════════════════════════════════════════════════════════

    private static void BuildDifficultyCanvas(PuzzleManager pm, Vector3 worldPos)
    {
        var root = new GameObject("DifficultyUI_Canvas");
        root.transform.position = worldPos;
        root.transform.rotation = Quaternion.identity;

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.AddComponent<CanvasScaler>();
        AddInteractiveRaycasters(root);

        var rootRT = root.GetComponent<RectTransform>();
        rootRT.sizeDelta  = new Vector2(620f, 480f);
        rootRT.localScale = Vector3.one * 0.003f;   // ≈ 1.86 m wide in world space

        Color panelBg = new Color(0.10f, 0.10f, 0.12f, 0.90f);

        // ── Step 1: Puzzle Type panel ─────────────────────────────────────
        var typePanel = MakePanel(root.transform, "PuzzleTypePanel", panelBg);

        MakeUIText(typePanel.transform, "Title", "Choose Puzzle",
                   new Vector2(0f, 160f), new Vector2(560f, 60f), 34, Color.white);

        var snowmanBtn = MakeUIButton(typePanel.transform, "SnowmanBtn", "Snowman",
                                      new Vector2(-140f, 40f), new Color(0.78f, 0.78f, 0.80f));
        var robotBtn   = MakeUIButton(typePanel.transform, "RobotBtn",   "Robot",
                                      new Vector2( 140f, 40f), new Color(0.55f, 0.68f, 0.82f));

        MakeUIText(typePanel.transform, "SubLabel", "Simple (3–7 pieces)  |  Complex (5–12 pieces)",
                   new Vector2(0f, -60f), new Vector2(560f, 40f), 18, new Color(0.7f, 0.7f, 0.7f));

        // ── Step 2: Difficulty panel ──────────────────────────────────────
        var diffPanel = MakePanel(root.transform, "DifficultyPanel", panelBg);
        diffPanel.SetActive(false);

        MakeUIText(diffPanel.transform, "Title", "Choose Difficulty",
                   new Vector2(0f, 160f), new Vector2(560f, 60f), 34, Color.white);

        var easyBtn   = MakeUIButton(diffPanel.transform, "EasyBtn",   "Easy",
                                      new Vector2(-190f, 50f), new Color(0.72f, 0.72f, 0.72f));
        var mediumBtn = MakeUIButton(diffPanel.transform, "MediumBtn", "Medium",
                                      new Vector2(   0f, 50f), new Color(0.60f, 0.60f, 0.62f));
        var hardBtn   = MakeUIButton(diffPanel.transform, "HardBtn",   "Hard",
                                      new Vector2( 190f, 50f), new Color(0.48f, 0.48f, 0.50f));

        // Sub-labels describing what each baseline means
        MakeUIText(diffPanel.transform, "EasyDesc",   "Full assist\nStrong magnet\nClear pieces",
                   new Vector2(-190f, -20f), new Vector2(155f, 55f), 13, new Color(0.60f, 0.60f, 0.62f));
        MakeUIText(diffPanel.transform, "MediumDesc", "Moderate assist\nLight magnet\nSubtle pieces",
                   new Vector2(   0f, -20f), new Vector2(155f, 55f), 13, new Color(0.60f, 0.60f, 0.62f));
        MakeUIText(diffPanel.transform, "HardDesc",   "Minimal assist\nNo magnet\nBlended pieces",
                   new Vector2( 190f, -20f), new Vector2(155f, 55f), 13, new Color(0.60f, 0.60f, 0.62f));

        MakeUIText(diffPanel.transform, "HintLabel",
                   "Sets your baseline assistance. The system adapts automatically to your readings.",
                   new Vector2(0f, -60f), new Vector2(560f, 40f), 17, new Color(0.65f, 0.65f, 0.65f));

        // ── Shared status label ────────────────────────────────────────────
        var statusLabel = MakeUIText(root.transform, "StatusLabel", "",
                                     new Vector2(0f, -200f), new Vector2(580f, 44f), 20,
                                     new Color(0.85f, 0.85f, 0.85f));

        // ── Wire DifficultyUI component ────────────────────────────────────
        var diffUI = root.AddComponent<DifficultyUI>();
        diffUI.puzzleTypePanel  = typePanel;
        diffUI.difficultyPanel  = diffPanel;
        diffUI.snowmanButton    = snowmanBtn;
        diffUI.robotButton      = robotBtn;
        diffUI.easyButton       = easyBtn;
        diffUI.mediumButton     = mediumBtn;
        diffUI.hardButton       = hardBtn;
        diffUI.statusLabel      = statusLabel;
        diffUI.puzzleManager    = pm;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Environment helpers
    // ════════════════════════════════════════════════════════════════════════

    private static void AddDirectionalLight(Color color, float intensity, Quaternion rotation)
    {
        var go = new GameObject("Directional Light");
        go.transform.rotation = rotation;
        var light = go.AddComponent<Light>();
        light.type      = LightType.Directional;
        light.color     = color;
        light.intensity = intensity;
    }

    private static void SetAmbientFlat(Color color)
    {
        RenderSettings.ambientMode  = AmbientMode.Flat;
        RenderSettings.ambientLight = color;
    }

    private static void AddFloor(Vector3 center, float roomW, float roomD, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "Floor";
        go.transform.position   = center + Vector3.down * 0.05f;
        go.transform.localScale = new Vector3(roomW + 0.4f, 0.1f, roomD + 0.4f);
        if (mat != null) go.GetComponent<Renderer>().material = mat;
    }

    private static void AddWalls(float roomW, float roomD, float wallH, Material mat)
    {
        float hw = roomW / 2f + 0.1f;
        float hd = roomD / 2f + 0.1f;
        float hy = wallH / 2f;
        AddBox("Wall_North", new Vector3(  0,  hy,  hd), new Vector3(roomW + 0.2f, wallH, 0.2f), mat);
        AddBox("Wall_South", new Vector3(  0,  hy, -hd), new Vector3(roomW + 0.2f, wallH, 0.2f), mat);
        AddBox("Wall_East",  new Vector3( hw,  hy,   0), new Vector3(0.2f, wallH, roomD + 0.2f), mat);
        AddBox("Wall_West",  new Vector3(-hw,  hy,   0), new Vector3(0.2f, wallH, roomD + 0.2f), mat);
    }

    private static GameObject AddBox(string name, Vector3 pos, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.position   = pos;
        go.transform.localScale = scale;
        if (mat != null) go.GetComponent<Renderer>().material = mat;
        return go;
    }

    /// Roof so rooms feel enclosed (and pieces can't be lifted out over the walls). It's
    /// a cube primitive, so it carries a BoxCollider that also caps the room physically.
    private static void AddCeiling(float roomW, float roomD, float wallH, Material mat)
    {
        AddBox("Ceiling", new Vector3(0f, wallH + 0.05f, 0f),
               new Vector3(roomW + 0.4f, 0.1f, roomD + 0.4f), mat);
    }

    private static Light AddPointLight(string name, Vector3 pos, Color color, float intensity, float range)
    {
        var go = new GameObject(name);
        go.transform.position = pos;
        var l = go.AddComponent<Light>();
        l.type      = LightType.Point;
        l.color     = color;
        l.intensity = intensity;
        l.range     = range;
        l.shadows   = LightShadows.None;
        return l;
    }

    /// Bright, even interior lighting for a ROOFED room: a soft directional for shape
    /// plus four ceiling fill lights so panels/text read clearly and the space never
    /// feels claustrophobic. Sets a generous flat ambient and RETURNS every light created
    /// so callers can switch them off (e.g. the hard-mode darkroom).
    private static List<Light> AddRoomLighting(float roomW, float roomD, float wallH, Color ambient)
    {
        SetAmbientFlat(ambient);
        var lights = new List<Light>();

        var dirGO = new GameObject("Directional Light");
        dirGO.transform.rotation = Quaternion.Euler(50f, 25f, 0f);
        var dir = dirGO.AddComponent<Light>();
        dir.type      = LightType.Directional;
        dir.color     = new Color(1f, 0.98f, 0.95f);
        dir.intensity = 0.7f;
        dir.shadows   = LightShadows.None;
        lights.Add(dir);

        float qx = roomW * 0.25f, qz = roomD * 0.25f, y = wallH - 0.25f;
        Vector3[] spots =
        {
            new Vector3(-qx, y, -qz), new Vector3(qx, y, -qz),
            new Vector3(-qx, y,  qz), new Vector3(qx, y,  qz),
        };
        foreach (var p in spots)
            lights.Add(AddPointLight("CeilingLight", p, new Color(1f, 0.96f, 0.90f), 1.4f, 7f));

        return lights;
    }

    /// A grabbable forearm lantern + spotlight for the hard (dark) puzzle. Starts
    /// inactive; HardModeDarkroom enables it only on Hard. See [[ArmLantern]].
    private static GameObject BuildLantern(Vector3 pos)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = "Lantern";
        go.transform.position   = pos;
        go.transform.localScale = new Vector3(0.08f, 0.10f, 0.08f);
        // Emissive body so the lantern glows and reads as a light source in the dark.
        go.GetComponent<Renderer>().material =
            GetOrCreateEmissiveMat("Lantern", new Color(1f, 0.82f, 0.40f), 2.5f);

        go.AddComponent<Rigidbody>().mass = 0.3f;
        go.AddComponent<XRGrabInteractable>();

        // Soft glow so the lantern actually emits a small pool of light (easy to find) and
        // lights the player's hand area once attached — small range keeps the room dark.
        var glowGO = new GameObject("LanternGlow");
        glowGO.transform.SetParent(go.transform, false);
        var glow = glowGO.AddComponent<Light>();
        glow.type      = LightType.Point;
        glow.color     = new Color(1f, 0.85f, 0.55f);
        glow.intensity = 1.6f;
        glow.range     = 1.8f;
        glow.shadows   = LightShadows.None;

        // Beam — OFF until grabbed (ArmLantern enables it). Aims along the lantern's local
        // forward, which becomes the arm direction once clipped to the forearm.
        var spotGO = new GameObject("LanternSpot");
        spotGO.transform.SetParent(go.transform, false);
        var spot = spotGO.AddComponent<Light>();
        spot.type      = LightType.Spot;
        spot.color     = new Color(1f, 0.93f, 0.75f);
        spot.intensity = 6f;
        spot.range     = 12f;
        spot.spotAngle = 75f;
        spot.shadows   = LightShadows.None;
        spot.enabled   = false;

        var lantern = go.AddComponent<ArmLantern>();
        lantern.beam = spot;
        go.SetActive(false);
        return go;
    }

    /// Spawns the XR rig at <paramref name="pos"/> and yaws it so the player's default
    /// forward faces <paramref name="faceTarget"/> (the panel/table). Yaw only, so the
    /// horizon stays level. NOTE: in VR the headset's real orientation is applied on top
    /// of this, so the player should recenter (or use a recenter-on-start) to actually
    /// face the content — this just sets the authored default.
    private static void SpawnXRRig(Vector3 pos, Vector3 faceTarget)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(k_XRRigPrefab);
        if (prefab == null)
        {
            Debug.LogWarning($"[PuzzleSceneBuilder] XR Rig prefab not found:\n  {k_XRRigPrefab}");
            return;
        }
        var rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        rig.transform.position = pos;

        Vector3 flat = faceTarget - pos; flat.y = 0f;
        if (flat.sqrMagnitude > 0.0001f)
            rig.transform.rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);

        // Recenter the player's head to this spawn pose at runtime so a scene transition
        // (e.g. Tutorial → Game) can't leave them standing on the wrong side of the room.
        var recenter = rig.GetComponent<XRSpawnRecenter>();
        if (recenter == null) recenter = rig.AddComponent<XRSpawnRecenter>();
        recenter.spawnPosition = pos;
        recenter.faceTarget    = faceTarget;
    }

    private static void AddWorldText(string goName, Vector3 pos, string text)
    {
        var go = new GameObject(goName);
        go.transform.position = pos;
        var c  = go.AddComponent<Canvas>();
        c.renderMode = RenderMode.WorldSpace;
        go.AddComponent<CanvasScaler>();
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta  = new Vector2(600f, 100f);
        rt.localScale = Vector3.one * 0.003f;
        MakeUIText(go.transform, "Label", text, Vector2.zero, new Vector2(580f, 90f), 26,
                   new Color(0.55f, 0.55f, 0.55f));
    }

    // ════════════════════════════════════════════════════════════════════════
    // UI helpers
    // ════════════════════════════════════════════════════════════════════════

    /// World-space UI needs both raycasters: GraphicRaycaster for mouse (Editor) and
    /// TrackedDeviceGraphicRaycaster for VR controller rays on the Quest.
    private static void AddInteractiveRaycasters(GameObject canvasGO)
    {
        canvasGO.AddComponent<GraphicRaycaster>();
        canvasGO.AddComponent<TrackedDeviceGraphicRaycaster>();
    }

    private static GameObject MakePanel(Transform parent, string name, Color bgColor)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        go.AddComponent<Image>().color = bgColor;
        return go;
    }

    private static TextMeshProUGUI MakeUIText(Transform parent, string name, string text,
        Vector2 anchoredPos, Vector2 size, int fontSize, Color color)
    {
        var go  = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt  = go.AddComponent<RectTransform>();
        rt.sizeDelta        = size;
        rt.anchoredPosition = anchoredPos;
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text      = text;
        tmp.fontSize  = fontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color     = color;
        return tmp;
    }

    private static Button MakeUIButton(Transform parent, string name, string label,
        Vector2 anchoredPos, Color bgColor)
    {
        var go  = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt  = go.AddComponent<RectTransform>();
        rt.sizeDelta        = new Vector2(160f, 72f);
        rt.anchoredPosition = anchoredPos;
        var img = go.AddComponent<Image>();
        img.color = bgColor;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        MakeUIText(go.transform, "Label", label, Vector2.zero, new Vector2(150f, 62f), 24, Color.white);
        return btn;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Material helper
    // ════════════════════════════════════════════════════════════════════════

    private static Material GetOrCreateMat(string matName, Color color, bool transparent = false)
    {
        var path = $"{k_MatDir}/{matName}.mat";
        var mat  = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;

        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        mat = new Material(shader) { name = matName };

        if (transparent)
        {
            mat.SetFloat("_Surface",   1f);
            mat.SetFloat("_Blend",     0f);
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite",   0);
            mat.renderQueue = 3000;
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
        }

        mat.color = color;
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    /// Self-illuminated material — renders its colour as emission so the object stays
    /// visible even in a pitch-black room without lighting its surroundings. Applies the
    /// emission even to an already-existing material asset (an earlier build may have
    /// created a non-emissive one with the same name).
    private static Material GetOrCreateEmissiveMat(string matName, Color color, float emission)
    {
        var path  = $"{k_MatDir}/{matName}.mat";
        var mat   = AssetDatabase.LoadAssetAtPath<Material>(path);
        bool isNew = mat == null;
        if (isNew)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            mat = new Material(shader) { name = matName };
        }

        mat.color = color;
        mat.EnableKeyword("_EMISSION");
        mat.SetColor("_EmissionColor", color * emission);
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;

        if (isNew) AssetDatabase.CreateAsset(mat, path);
        else       EditorUtility.SetDirty(mat);
        return mat;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Build Settings + folder helpers
    // ════════════════════════════════════════════════════════════════════════

    private static void AddScenesToBuildSettings(params string[] scenePaths)
    {
        var existing = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        foreach (var path in scenePaths)
            if (!existing.Exists(s => s.path == path))
                existing.Add(new EditorBuildSettingsScene(path, true));
        EditorBuildSettings.scenes = existing.ToArray();
    }

    private static void EnsureFolderPath(string path)
    {
        var parts   = path.Split('/');
        var current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            var next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }
}
