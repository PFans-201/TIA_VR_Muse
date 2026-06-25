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
///   • Robot puzzle — one ordered list of 22 pieces; Easy 5 / Medium 12 / Hard 22
///   • All pieces start INACTIVE; PuzzleManager activates the first N for the chosen difficulty
///   • Zen grey environment: walls 87 %, floor 82 %, table near-white
///   • Soft neutral directional light + grey ambient
///   • CognitiveLoadAdapter + PieceHintSystem for MUSE S integration
///   • Single-step DifficultyUI (Easy / Medium / Hard) with an info toggle
///   • HardModeDarkroom (dark on Medium+Hard, obstacles on Hard) + forearm lantern
public static class PuzzleSceneBuilder
{
    // ── Asset paths ──────────────────────────────────────────────────────────
    private const string k_MatDir       = "Assets/Materials/PuzzleGame";
    private const string k_EntryScene   = "Assets/Scenes/EntryHall.unity";
    private const string k_TutScene     = "Assets/Scenes/TutorialRoom.unity";
    private const string k_ZenScene     = "Assets/Scenes/ZenPuzzleRoom.unity";
    private const string k_XRRigPrefab  = "Assets/VRTemplateAssets/Prefabs/Setup/Complete XR Origin Set Up Variant.prefab";

    // Optional hand-authored robot model. If present, the builder splits its direct children
    // into grabbable pieces (each child = one piece, generating its own ghost/snap zone) instead
    // of the built-in primitive robot. Author one child per piece, named "01_Head", "02_Torso", …
    // (numeric prefix = difficulty order: Easy uses the first N, Medium more, Hard all).
    private const string k_RobotPrefabDir = "Assets/Prefabs/PuzzleGame";
    private const string k_RobotPrefab    = "Assets/Prefabs/PuzzleGame/RobotModel.prefab";
    private const string k_RobotAuthScene = "Assets/Scenes/_RobotAuthoring.unity";

    // Optional per-difficulty robots. If a level's prefab is present, the WHOLE prefab is that
    // level's puzzle and the piece count shown in the UI = its child count (no first-N threshold).
    // A level with no prefab here falls back to the single k_RobotPrefab / primitive threshold.
    private const string k_RobotPrefabEasy   = "Assets/Prefabs/PuzzleGame/RobotModel_Easy.prefab";
    private const string k_RobotPrefabMedium = "Assets/Prefabs/PuzzleGame/RobotModel_Medium.prefab";
    private const string k_RobotPrefabHard   = "Assets/Prefabs/PuzzleGame/RobotModel_Hard.prefab";

    // Redesigned 3-scene session flow (Intro → Tutorial → Game).
    private const string k_IntroScene   = "Assets/Scenes/01_Intro.unity";
    private const string k_Tut2Scene    = "Assets/Scenes/02_Tutorial.unity";
    private const string k_GameScene    = "Assets/Scenes/03_Game.unity";
    private const string k_SimPrefab    = "Assets/Samples/XR Interaction Toolkit/3.3.1/XR Interaction Simulator/XR Interaction Simulator.prefab";

    // EEG transport baked into the session-flow build. Default = WiFi UDP bridge (a PC runs the
    // Python bridge and streams stress to the Quest over WiFi, ports 5005/5006); the dedicated
    // "on-device BLE" menu flips this for a standalone build that connects to the Muse itself.
    private static bool s_useUdpBridge = true;

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

    /// Adds the head-locked Muse status HUD to the open scene if absent. The HUD is now baked
    /// into every session-flow scene at build time, so this is just the per-scene helper.
    private static void AddMuseStatusHud()
    {
        if (Object.FindFirstObjectByType<MuseStatusHUD>(FindObjectsInactive.Include) == null)
            new GameObject("MuseStatusHUD").AddComponent<MuseStatusHUD>();
    }

    /// (internal helper) Adds the XR Device Simulator to every scene so you can drive the headset
    /// + controllers (and GRAB objects / click UI) with mouse + keyboard in the Editor, no headset
    /// needed. Wrapped in EditorOnlyObject so it self-destroys in Quest builds. Invoked by the
    /// "Prepare for PC Debug" menu.
    private static void AddXRDeviceSimulatorAllScenes()
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
    /// ONE-CLICK prep for the APK (WiFi UDP-bridge EEG path — the default deployment). Rebuilds
    /// the 3 session-flow scenes (Intro → Tutorial → Game), applies the VR-UI fix, wires the UDP
    /// EEG source and bakes the Muse status HUD into each scene. Run this after changing code,
    /// just before File ▸ Build And Run.
    [MenuItem("Puzzle Game/Prepare for APK Build — UDP bridge (default)", priority = 0)]
    public static void PrepareForApkBuild() => BuildSessionFlowScenes();

    /// APK prep for the on-device BLE path (no PC bridge — the Quest connects to the Muse itself).
    /// Same scenes, but the baked EEG source is direct BLE instead of the UDP bridge.
    [MenuItem("Puzzle Game/Prepare for APK Build — on-device BLE", priority = 1)]
    public static void BuildSessionFlowScenesBle()
    {
        s_useUdpBridge = false;
        try { BuildSessionFlowScenes(); }
        finally { s_useUdpBridge = true; }   // restore the UDP default
    }

    /// Prep for Editor testing with mouse + keyboard: builds the UDP scenes, then drops in the
    /// XR Device Simulator (Editor-only, self-destroys in Quest builds) so you can drive the rig
    /// without a headset. Enter Play mode after running this.
    [MenuItem("Puzzle Game/Prepare for PC Debug (XR Simulator)", priority = 2)]
    public static void PrepareForPcDebug()
    {
        BuildSessionFlowScenes();
        AddXRDeviceSimulatorAllScenes();
        Debug.Log("[PuzzleSceneBuilder] PC-debug prep done — enter Play mode to drive the rig with mouse/keyboard.");
    }

    // ── Robot authoring workflow ───────────────────────────────────────────────
    // Hand-shape the robot model that the game splits into puzzle pieces. The editable
    // model lives in one prefab (k_RobotPrefab); when present the builder uses it instead
    // of the built-in primitive robot. These three menus are the whole loop:
    //   ① open a sandbox scene seeded with the current 22 parts (+ on-screen instructions)
    //   ② save the edited "RobotModel" root back to the prefab
    //   ↻ then run a normal "Prepare for APK Build…" — it rebuilds the room from the prefab.

    // Root-name → prefab-path map: one editable robot per difficulty.
    private static readonly (string root, string path)[] k_RobotRootMap =
    {
        ("RobotModel_Easy",   k_RobotPrefabEasy),
        ("RobotModel_Medium", k_RobotPrefabMedium),
        ("RobotModel_Hard",   k_RobotPrefabHard),
    };

    /// Opens a sandbox scene seeded with three editable robots — RobotModel_Easy / _Medium / _Hard
    /// — standing on the floor, each beside a height pole, for authoring one robot per difficulty.
    [MenuItem("Puzzle Game/Robot Authoring/① Open Authoring Scene", priority = 40)]
    public static void OpenRobotAuthoringScene()
    {
        if (BlockedByPlayMode()) return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        EnsureFolderPath(k_MatDir);
        BuildRobotAuthoringScene();
    }

    /// Saves every robot root found in the open scene (RobotModel_Easy/_Medium/_Hard) to its
    /// prefab. Authoring helpers (floor, poles, panel, lights) sit outside the roots, so they
    /// are excluded.
    [MenuItem("Puzzle Game/Robot Authoring/② Save Robots → Prefabs", priority = 41)]
    public static void SaveRobotModelsToPrefabs()
    {
        if (BlockedByPlayMode()) return;
        EnsureFolderPath(k_RobotPrefabDir);

        int saved = 0;
        Object lastSaved = null;
        foreach (var (rootName, path) in k_RobotRootMap)
        {
            var root = GameObject.Find(rootName);
            if (root == null) continue;
            var pf = PrefabUtility.SaveAsPrefabAsset(root, path, out bool ok);
            if (ok && pf != null)
            {
                saved++; lastSaved = pf;
                Debug.Log($"[PuzzleSceneBuilder] Saved '{rootName}' → '{path}' ({root.transform.childCount} pieces).");
            }
            else Debug.LogError($"[PuzzleSceneBuilder] Failed to save '{rootName}' → '{path}'.");
        }

        AssetDatabase.Refresh();
        if (saved == 0)
        {
            EditorUtility.DisplayDialog("No robot roots found",
                "This scene has no root named 'RobotModel_Easy', 'RobotModel_Medium' or " +
                "'RobotModel_Hard'.\n\nOpen the authoring scene (Robot Authoring ▸ ① Open " +
                "Authoring Scene), or rename your model roots to those, then try again.", "OK");
            return;
        }
        if (lastSaved != null) { EditorGUIUtility.PingObject(lastSaved); Selection.activeObject = lastSaved; }
        Debug.Log($"[PuzzleSceneBuilder] Saved {saved} robot prefab(s). " +
                  "Now run a 'Prepare for APK Build…' menu to rebuild the room from them.");
    }

    /// One-click: write the three per-difficulty placeholder prefabs without touching the open scene.
    [MenuItem("Puzzle Game/Robot Authoring/Generate Placeholder Prefabs (no scene)", priority = 60)]
    public static void GenerateRobotPerDifficultyPlaceholders()
    {
        if (BlockedByPlayMode()) return;
        EnsureFolderPath(k_MatDir);
        EnsureFolderPath(k_RobotPrefabDir);

        WritePlaceholderPrefab("RobotModel_Easy",   5,  k_RobotPrefabEasy);
        WritePlaceholderPrefab("RobotModel_Medium", 12, k_RobotPrefabMedium);
        WritePlaceholderPrefab("RobotModel_Hard",   22, k_RobotPrefabHard);
        AssetDatabase.Refresh();
    }

    /// Builds a placeholder robot root, saves it to a prefab, and discards the scene object.
    private static void WritePlaceholderPrefab(string rootName, int maxPieces, string path)
    {
        var root  = BuildRobotModelRoot(rootName, maxPieces);
        var saved = PrefabUtility.SaveAsPrefabAsset(root, path, out bool ok);
        Object.DestroyImmediate(root);
        if (ok && saved != null)
        {
            EditorGUIUtility.PingObject(saved);
            Debug.Log($"[PuzzleSceneBuilder] Placeholder '{rootName}' → '{path}' " +
                      $"({(maxPieces == int.MaxValue ? "all" : maxPieces.ToString())} parts).");
        }
        else Debug.LogError($"[PuzzleSceneBuilder] Failed to write placeholder '{path}'.");
    }

    // Worker for the prep menus above (UDP by default; the BLE menu flips s_useUdpBridge).
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
        AddEegSource();   // UDP bridge (default) or on-device BLE, per the build menu used

        var intro = new GameObject("IntroController").AddComponent<IntroController>();
        intro.nextScene   = "02_Tutorial";
        intro.restSeconds = 20f;
        BuildIntroUI(intro, new Vector3(0f, 1.6f, 1.6f));

        AddMuseStatusHud();   // phase-aware Muse HUD (raw signal during baseline, indices in game)
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
        AddEegSource();   // singleton-safe: the Intro scene's source persists; this seeds isolated tests

        // Grab practice shelf + active grab objects (the persistent EEG adapter from the intro
        // scene carries over and feeds this scene's CognitiveLoadAdapter).
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
            // Match the GAME pieces' physics so practice feels identical: interpolated,
            // continuous-dynamic collision (no tunnelling through the shelf/walls) and
            // velocity-tracked grabbing (the held object collides instead of ghosting through).
            var grb = go.AddComponent<Rigidbody>();
            grb.mass          = 0.15f;
            grb.interpolation = RigidbodyInterpolation.Interpolate;
            grb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            go.AddComponent<XRGrabInteractable>().movementType =
                XRBaseInteractable.MovementType.VelocityTracking;
            gx += 0.37f;
        }
        AddBox("Pedestal_Left",  new Vector3(-0.7f, 0.55f, 0.5f), new Vector3(0.30f, 1.1f, 0.30f), tableMat);
        AddBox("Pedestal_Right", new Vector3( 0.7f, 0.55f, 0.5f), new Vector3(0.30f, 1.1f, 0.30f), tableMat);

        var tut = new GameObject("TutorialController").AddComponent<TutorialController>();
        tut.nextScene  = "03_Game";
        tut.minSeconds = 40f;
        BuildTutorialFlowUI(tut, new Vector3(0f, 1.8f, 2.8f));

        AddMuseStatusHud();   // phase-aware Muse HUD (raw signal during baseline, indices in game)
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
                   "While holding a piece, push the thumbstick to ROTATE it.\n\n" +
                   "Take your time getting comfortable.",
                   new Vector2(0f, -20f), new Vector2(660f, 250f), 23, new Color(0.22f, 0.22f, 0.22f));

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
        if (s_useUdpBridge) ApplyUdpBridge();   // WiFi UDP source (default)
        else                ApplyDirectBle();   // on-device BLE source
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

    /// EEG source for the session-flow scenes, chosen by s_useUdpBridge:
    ///   UDP bridge (default) — a MuseUdpAdapter (PC streams stress over WiFi, ports 5005/5006);
    ///   on-device BLE        — a MuseDirectAdapter + BLE transport (Quest connects to the Muse).
    /// Both are DontDestroyOnLoad singletons, so the first scene's source carries through the
    /// flow and any later duplicate self-destroys on Awake. cognitiveLoad is left null so the
    /// adapter auto-targets whichever scene's CognitiveLoadAdapter is active.
    private static void AddEegSource()
    {
        if (s_useUdpBridge)
        {
            if (Object.FindFirstObjectByType<MuseUdpAdapter>() != null) return;
            var go  = new GameObject("MuseUdpAdapter");
            var udp = go.AddComponent<MuseUdpAdapter>();
            udp.port        = 5005;
            udp.controlPort = 5006;
        }
        else
        {
            if (Object.FindFirstObjectByType<MuseDirectAdapter>() != null) return;
            var go     = new GameObject("MuseDirectAdapter");
            var direct = go.AddComponent<MuseDirectAdapter>();
            go.AddComponent<VelorexeBleTransport>();
            direct.deviceNameContains = "Muse";
        }
    }

    /// Game-scene EEG wiring for the UDP-bridge path: disables the puzzle template's BrainFlow
    /// (MuseAthenaAdapter) and any direct-BLE source so they can't fight the UDP stream or fail
    /// on the Quest, then ensures a MuseUdpAdapter is present. Singleton-safe — the persistent
    /// adapter from the Intro scene wins at runtime; the baked one self-destructs.
    private static bool ApplyUdpBridge()
    {
        var cola = Object.FindFirstObjectByType<CognitiveLoadAdapter>();
        if (cola == null) return false;                                       // scene doesn't consume stress

        var athena = Object.FindFirstObjectByType<MuseAthenaAdapter>();
        if (athena != null) athena.enabled = false;
        var direct = Object.FindFirstObjectByType<MuseDirectAdapter>();
        if (direct != null) direct.enabled = false;

        if (Object.FindFirstObjectByType<MuseUdpAdapter>() != null) return false;
        var go  = new GameObject("MuseUdpAdapter");
        var udp = go.AddComponent<MuseUdpAdapter>();
        udp.cognitiveLoad = cola;
        udp.port          = 5005;
        udp.controlPort   = 5006;
        return true;
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
        phs.puzzleManager = pm;   // hint system reads IsDarkRoom for the find-me flicker
        adc.puzzleManager = pm;   // wire adaptive controller → manager

        var anchorGO = new GameObject("PuzzleAnchor");
        anchorGO.transform.position = new Vector3(0f, 1.01f, 0f);
        pm.puzzleAnchor = anchorGO.transform;

        // ── Robot puzzle ──────────────────────────────────────────────────
        //   Source priority per difficulty:
        //     1. its own per-difficulty prefab (RobotModel_<Level>) → whole prefab, count = children
        //     2. the shared single prefab (RobotModel)              → first-N threshold
        //     3. the built-in primitive robot                       → first-N threshold
        var snapRootRobot = new GameObject("SnapZones_Robot");
        BuildRobotPieceSets(pm, snapRootRobot.transform);

        // ── Difficulty UI (single step, robot only) ───────────────────────
        //   Built AFTER the piece sets so the "N pieces" labels reflect the resolved counts.
        BuildDifficultyCanvas(pm, new Vector3(0f, 1.8f, -1.8f));

        // ── Ambient instruction text ──────────────────────────────────────
        AddWorldText("RoomLabel", new Vector3(0f, 2.85f, -3.8f), "Assemble the puzzle");

        // ── Keep pieces inside the room (backstop for grabbed pieces) ──────
        var contGO = new GameObject("RoomPieceContainer");
        var cont   = contGO.AddComponent<RoomPieceContainer>();
        cont.interiorCenter = new Vector3(0f, 1.4f, 0f);
        cont.interiorSize   = new Vector3(7.4f, 2.7f, 7.4f);

        // ── Hard-mode obstacles (walls / columns / baskets) — shown only on Hard ──
        var obstacles = BuildHardObstacles();

        // ── Dark room (Medium + Hard) + grabbable forearm lantern ─────────
        var lantern = BuildLantern(new Vector3(0f, 1.2f, -1.5f));   // floats in front of the player, reachable
        var darkGO  = new GameObject("HardModeDarkroom");
        var dark    = darkGO.AddComponent<HardModeDarkroom>();
        dark.roomLights    = roomLights.ToArray();
        dark.lantern       = lantern;
        dark.obstacleRoot  = obstacles;
        dark.puzzleManager = pm;
        dark.litAmbient    = litAmbient;

        // ── Adaptive-event HUD: shows each adaptive action + its trigger (Muse vs behaviour) ──
        new GameObject("AdaptiveEventHUD").AddComponent<AdaptiveEventHUD>();
        AddMuseStatusHud();   // phase-aware Muse HUD (indices during gameplay)

        EditorSceneManager.SaveScene(scene, k_ZenScene);
        Debug.Log($"[PuzzleSceneBuilder] Saved {k_ZenScene}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Puzzle set builder — shared by both puzzle types
    // ════════════════════════════════════════════════════════════════════════

    private static List<GameObject> BuildPuzzlePieces(
        (string n, PrimitiveType p, Vector3 pos, Vector3 euler, Vector3 scale, Color col)[] defs,
        Transform snapRoot)
    {
        var allPieces = new List<GameObject>();
        foreach (var d in defs)
        {
            var (pieceGO, _) = MakePuzzlePiece($"Robot_{d.n}", d.p, d.pos, d.euler, d.scale, d.col, snapRoot);
            pieceGO.SetActive(false);   // inactive until the player selects a difficulty
            allPieces.Add(pieceGO);
        }
        return allPieces;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Robot model definition + authoring scene
    // ════════════════════════════════════════════════════════════════════════

    /// The built-in 22-part robot, ordered for the difficulty ramp (Easy 5 / Medium 12 / Hard 22).
    /// Single source of truth shared by the primitive fallback and the authoring/placeholder tools.
    /// The 12 main parts come first (recognisable anatomy for Easy/Medium); detail sub-pieces follow.
    /// (n, primitive, world pos, euler, scale, colour)
    private static (string n, PrimitiveType p, Vector3 pos, Vector3 euler, Vector3 scale, Color col)[] RobotPrimitiveDefs()
    {
        return new (string n, PrimitiveType p, Vector3 pos, Vector3 euler, Vector3 scale, Color col)[]
        {
            // Easy (5): Head, Torso, LeftArm, RightArm, LeftLeg
            ("Head",         PrimitiveType.Cube,     new Vector3( 0.00f, 1.90f,  0.00f), Vector3.zero,             new Vector3(0.26f, 0.26f, 0.22f), new Color(0.60f, 0.72f, 0.85f)),
            ("Torso",        PrimitiveType.Cube,     new Vector3( 0.00f, 1.52f,  0.00f), Vector3.zero,             new Vector3(0.38f, 0.36f, 0.22f), new Color(0.55f, 0.65f, 0.75f)),
            ("LeftArm",      PrimitiveType.Cylinder, new Vector3(-0.32f, 1.58f,  0.00f), new Vector3(0f, 0f,  80f), new Vector3(0.09f, 0.25f, 0.09f), new Color(0.62f, 0.68f, 0.72f)),
            ("RightArm",     PrimitiveType.Cylinder, new Vector3( 0.32f, 1.58f,  0.00f), new Vector3(0f, 0f, -80f), new Vector3(0.09f, 0.25f, 0.09f), new Color(0.62f, 0.68f, 0.72f)),
            ("LeftLeg",      PrimitiveType.Cylinder, new Vector3(-0.12f, 1.20f,  0.00f), Vector3.zero,             new Vector3(0.10f, 0.28f, 0.10f), new Color(0.50f, 0.52f, 0.55f)),
            // Medium adds (12): + RightLeg, LeftForearm, RightForearm, feet, eyes
            ("RightLeg",     PrimitiveType.Cylinder, new Vector3( 0.12f, 1.20f,  0.00f), Vector3.zero,             new Vector3(0.10f, 0.28f, 0.10f), new Color(0.50f, 0.52f, 0.55f)),
            ("LeftForearm",  PrimitiveType.Cylinder, new Vector3(-0.50f, 1.38f,  0.00f), new Vector3(0f, 0f,  65f), new Vector3(0.07f, 0.20f, 0.07f), new Color(0.58f, 0.62f, 0.65f)),
            ("RightForearm", PrimitiveType.Cylinder, new Vector3( 0.50f, 1.38f,  0.00f), new Vector3(0f, 0f, -65f), new Vector3(0.07f, 0.20f, 0.07f), new Color(0.58f, 0.62f, 0.65f)),
            ("LeftFoot",     PrimitiveType.Cube,     new Vector3(-0.12f, 0.97f,  0.06f), Vector3.zero,             new Vector3(0.16f, 0.07f, 0.24f), new Color(0.42f, 0.44f, 0.46f)),
            ("RightFoot",    PrimitiveType.Cube,     new Vector3( 0.12f, 0.97f,  0.06f), Vector3.zero,             new Vector3(0.16f, 0.07f, 0.24f), new Color(0.42f, 0.44f, 0.46f)),
            ("LeftEye",      PrimitiveType.Sphere,   new Vector3(-0.07f, 1.96f,  0.12f), Vector3.zero,             new Vector3(0.055f,0.055f,0.055f), new Color(0.08f, 0.08f, 0.10f)),
            ("RightEye",     PrimitiveType.Sphere,   new Vector3( 0.07f, 1.96f,  0.12f), Vector3.zero,             new Vector3(0.055f,0.055f,0.055f), new Color(0.08f, 0.08f, 0.10f)),
            // Hard adds (22): detail sub-pieces — panels, shoulders, neck, antenna, hands, hip, bolts
            ("ChestPlate",   PrimitiveType.Cube,     new Vector3( 0.00f, 1.56f,  0.115f), Vector3.zero,            new Vector3(0.24f, 0.22f, 0.03f), new Color(0.66f, 0.74f, 0.82f)),
            ("BackPlate",    PrimitiveType.Cube,     new Vector3( 0.00f, 1.56f, -0.115f), Vector3.zero,            new Vector3(0.24f, 0.22f, 0.03f), new Color(0.48f, 0.55f, 0.62f)),
            ("LeftShoulder", PrimitiveType.Sphere,   new Vector3(-0.26f, 1.66f,  0.00f), Vector3.zero,             new Vector3(0.12f, 0.12f, 0.12f), new Color(0.70f, 0.74f, 0.78f)),
            ("RightShoulder",PrimitiveType.Sphere,   new Vector3( 0.26f, 1.66f,  0.00f), Vector3.zero,             new Vector3(0.12f, 0.12f, 0.12f), new Color(0.70f, 0.74f, 0.78f)),
            ("Neck",         PrimitiveType.Cylinder, new Vector3( 0.00f, 1.74f,  0.00f), Vector3.zero,             new Vector3(0.09f, 0.05f, 0.09f), new Color(0.52f, 0.56f, 0.60f)),
            ("Antenna",      PrimitiveType.Cylinder, new Vector3( 0.00f, 2.10f,  0.00f), Vector3.zero,             new Vector3(0.02f, 0.10f, 0.02f), new Color(0.85f, 0.40f, 0.30f)),
            ("LeftHand",     PrimitiveType.Cube,     new Vector3(-0.62f, 1.22f,  0.00f), Vector3.zero,             new Vector3(0.09f, 0.09f, 0.09f), new Color(0.60f, 0.64f, 0.68f)),
            ("RightHand",    PrimitiveType.Cube,     new Vector3( 0.62f, 1.22f,  0.00f), Vector3.zero,             new Vector3(0.09f, 0.09f, 0.09f), new Color(0.60f, 0.64f, 0.68f)),
            ("Hip",          PrimitiveType.Cube,     new Vector3( 0.00f, 1.34f,  0.00f), Vector3.zero,             new Vector3(0.30f, 0.10f, 0.20f), new Color(0.46f, 0.50f, 0.54f)),
            ("ChestBolt",    PrimitiveType.Sphere,   new Vector3( 0.00f, 1.49f,  0.15f), Vector3.zero,             new Vector3(0.05f, 0.05f, 0.05f), new Color(0.90f, 0.78f, 0.30f)),
        };
    }

    /// Builds an editable robot root: one plain primitive child per part, named with a numeric
    /// prefix ("01_Head" …). Visual only — the game build adds grab/physics/ghost. Caller owns it.
    ///   rootName  — GameObject name (also the prefab key the smart-save matches on).
    ///   maxPieces — seed only the first N parts (e.g. 5 Easy / 12 Medium / 22 Hard).
    private static GameObject BuildRobotModelRoot(string rootName = "RobotModel", int maxPieces = int.MaxValue)
    {
        var root = new GameObject(rootName);
        root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        int idx = 1;
        foreach (var d in RobotPrimitiveDefs())
        {
            if (idx > maxPieces) break;
            var go = GameObject.CreatePrimitive(d.p);
            go.name = $"{idx:00}_{d.n}";
            go.transform.SetParent(root.transform, worldPositionStays: true);
            go.transform.position   = d.pos;
            go.transform.rotation   = Quaternion.Euler(d.euler);
            go.transform.localScale = d.scale;
            go.GetComponent<Renderer>().sharedMaterial = GetOrCreateMat($"Piece_Robot_{d.n}", d.col);
            idx++;
        }
        return root;
    }

    /// Sandbox scene: three editable robots (one per difficulty) standing on the floor, each beside
    /// a height pole, plus an instruction panel. The helpers sit OUTSIDE the robot roots, so
    /// "Save Robots → Prefabs" excludes them.
    private static void BuildRobotAuthoringScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Neutral lighting so shapes read clearly while editing.
        AddDirectionalLight(new Color(1f, 0.98f, 0.95f), 1.0f, Quaternion.Euler(50f, -30f, 0f));
        SetAmbientFlat(new Color(0.55f, 0.55f, 0.58f));

        var helpers = new GameObject("— Authoring Helpers (not saved) —");

        // Floor reference at y = 0 (a default plane is 10×10 m → scale ≈ width/10).
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "FloorReference";
        floor.transform.SetParent(helpers.transform, true);
        floor.transform.position   = Vector3.zero;
        floor.transform.localScale = new Vector3(0.9f, 1f, 0.4f);   // ≈ 9 × 4 m
        floor.GetComponent<Renderer>().sharedMaterial = GetOrCreateMat("Auth_Floor", new Color(0.82f, 0.82f, 0.84f));

        // Three editable robots side by side. Each is grounded on the floor with its own height pole.
        SeedAuthoringRobot(helpers.transform, "RobotModel_Easy",   5,  -2.2f, "EASY");
        SeedAuthoringRobot(helpers.transform, "RobotModel_Medium", 12,  0.0f, "MEDIUM");
        var hard = SeedAuthoringRobot(helpers.transform, "RobotModel_Hard", 22, 2.2f, "HARD");
        Selection.activeGameObject = hard;

        BuildAuthoringInstructions(helpers.transform, new Vector3(0f, 2.7f, 1.6f), Vector3.zero);

        EditorSceneManager.SaveScene(scene, k_RobotAuthScene);
        AssetDatabase.Refresh();
        Debug.Log($"[PuzzleSceneBuilder] Robot authoring scene ready → '{k_RobotAuthScene}'. " +
                  "Edit the RobotModel_Easy/_Medium/_Hard roots, then run " +
                  "Robot Authoring ▸ ② Save Robots → Prefabs.");
    }

    /// Builds one labelled editable robot, grounded on the floor at offsetX with a height pole that
    /// matches its top. Grounding shifts only the ROOT (layout-only): the build resets the prefab
    /// root to origin, so the in-game piece poses are unaffected. The label and pole are parented to
    /// HELPERS (never the robot) so they can never become pieces.
    private static GameObject SeedAuthoringRobot(Transform helpers, string rootName, int maxPieces,
                                                 float offsetX, string label)
    {
        var robot = BuildRobotModelRoot(rootName, maxPieces);

        // Drop the robot so its lowest point rests on the floor (y = 0).
        var rends = robot.GetComponentsInChildren<Renderer>();
        float footY = 0f, topY = 0f;
        if (rends.Length > 0)
        {
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            footY = b.min.y; topY = b.max.y;
        }
        float height = Mathf.Max(0.01f, topY - footY);
        robot.transform.position = new Vector3(offsetX, -footY, 0f);   // feet → 0; top → height

        // Height pole beside the robot, spanning 0 → its top (a default cylinder is 2 m tall).
        var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pole.name = $"HeightPole_{label}";
        pole.transform.SetParent(helpers, true);
        pole.transform.position   = new Vector3(offsetX - 0.6f, height * 0.5f, 0f);
        pole.transform.localScale = new Vector3(0.012f, height * 0.5f, 0.012f);
        pole.GetComponent<Renderer>().sharedMaterial = GetOrCreateMat("Auth_Pole", new Color(0.85f, 0.40f, 0.30f));
        Object.DestroyImmediate(pole.GetComponent<Collider>());

        // Floor label = difficulty only (piece count is whatever you leave in the prefab).
        var labelGO = new GameObject($"Label_{label}");
        labelGO.transform.SetParent(helpers, true);
        labelGO.transform.SetPositionAndRotation(new Vector3(offsetX, topY - footY + 0.25f, 0f), Quaternion.identity);
        var canvas = labelGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        labelGO.AddComponent<CanvasScaler>();
        var rt = labelGO.GetComponent<RectTransform>();
        rt.sizeDelta  = new Vector2(300f, 70f);
        rt.localScale = Vector3.one * 0.004f;
        MakeUIText(labelGO.transform, "Text", label,
                   Vector2.zero, new Vector2(300f, 70f), 32, new Color(0.95f, 0.95f, 0.55f));
        return robot;
    }

    /// World-space instruction card for the authoring scene.
    private static void BuildAuthoringInstructions(Transform parent, Vector3 worldPos, Vector3 euler)
    {
        var root = new GameObject("Instructions_Canvas");
        root.transform.SetParent(parent, true);
        root.transform.SetPositionAndRotation(worldPos, Quaternion.Euler(euler));

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.AddComponent<CanvasScaler>();

        var rt = root.GetComponent<RectTransform>();
        rt.sizeDelta  = new Vector2(760f, 580f);
        rt.localScale = Vector3.one * 0.0024f;   // ≈ 1.8 m wide

        var panel = MakePanel(root.transform, "Panel", new Color(0.10f, 0.10f, 0.12f, 0.92f));

        MakeUIText(panel.transform, "Title", "ROBOT AUTHORING",
                   new Vector2(0f, 250f), new Vector2(720f, 50f), 34, new Color(0.95f, 0.95f, 0.98f));

        const string body =
            "Three separate robots — one per difficulty:\n" +
            "  <b>RobotModel_Easy / _Medium / _Hard</b>.\n\n" +
            "Each is its OWN puzzle. The piece count shown in the\n" +
            "game is just how many children that robot has —\n" +
            "add or remove pieces freely.\n\n" +
            "• Reshape a piece: move / scale it, or swap its mesh.\n" +
            "• Add a piece: new child with a MeshRenderer\n" +
            "   (one mesh + one material is cleanest).\n" +
            "• Remove a piece: delete the child.\n\n" +
            "Each robot stands on the floor (y = 0); the red pole\n" +
            "beside it marks its height. You don't add\n" +
            "colliders/ghosts — the build does that.\n\n" +
            "When done:\n" +
            "  Robot Authoring ▸ ② Save Robots → Prefabs\n" +
            "  then a 'Prepare for APK Build…' menu to rebuild.";

        var t = MakeUIText(panel.transform, "Body", body,
                           new Vector2(0f, -25f), new Vector2(700f, 460f), 22, new Color(0.90f, 0.92f, 0.95f));
        t.alignment = TextAlignmentOptions.TopLeft;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Prefab-driven puzzle set — splits a hand-authored model into pieces
    // ════════════════════════════════════════════════════════════════════════
    //
    // The authored prefab is assembled (all pieces in their solved poses). Each DIRECT child
    // becomes one grabbable piece: the child keeps its authored mesh/material (the nicer look),
    // and the same shadow + snap-zone pipeline as the primitive robot is applied automatically.
    // Difficulty order comes from the numeric name prefix ("01_Head", "02_Torso", …); children
    // without a numeric prefix sort last (and keep their relative hierarchy order).

    /// Resolves and builds each difficulty's piece set onto the PuzzleManager, then syncs the
    /// per-difficulty pieceCount to the resolved counts (so the UI label reflects the prefab).
    private static void BuildRobotPieceSets(PuzzleManager pm, Transform snapRoot)
    {
        var easyPf   = AssetDatabase.LoadAssetAtPath<GameObject>(k_RobotPrefabEasy);
        var medPf    = AssetDatabase.LoadAssetAtPath<GameObject>(k_RobotPrefabMedium);
        var hardPf   = AssetDatabase.LoadAssetAtPath<GameObject>(k_RobotPrefabHard);
        var singlePf = AssetDatabase.LoadAssetAtPath<GameObject>(k_RobotPrefab);
        bool anyPerDiff = easyPf != null || medPf != null || hardPf != null;

        // Shared fallback list — only built if some level lacks its own prefab.
        bool needShared = !anyPerDiff || easyPf == null || medPf == null || hardPf == null;
        List<GameObject> shared = null;
        if (needShared)
        {
            shared = singlePf != null
                ? BuildPuzzlePiecesFromPrefab(singlePf, snapRoot)
                : BuildPuzzlePieces(RobotPrimitiveDefs(), snapRoot);
        }
        pm.robotPieces = shared ?? new List<GameObject>();

        if (!anyPerDiff)
        {
            // Legacy single-source mode: difficulties slice the shared list by pieceCount (unchanged).
            pm.easyPieces = new List<GameObject>();
            pm.mediumPieces = new List<GameObject>();
            pm.hardPieces = new List<GameObject>();
            Debug.Log(singlePf != null
                ? $"[PuzzleSceneBuilder] Robot from single prefab '{k_RobotPrefab}' ({shared.Count} pieces); " +
                  "difficulties use the first-N threshold."
                : "[PuzzleSceneBuilder] No robot prefab found — built-in primitive robot (first-N threshold). " +
                  $"Author per-difficulty prefabs (e.g. '{k_RobotPrefabEasy}') or a single '{k_RobotPrefab}'.");
            return;
        }

        // Per-difficulty mode: each level uses its own prefab in full, or the shared first-N fallback.
        pm.easyPieces   = ResolveLevelPieces(easyPf, snapRoot, shared, pm.easySettings.pieceCount);
        pm.mediumPieces = ResolveLevelPieces(medPf,  snapRoot, shared, pm.mediumSettings.pieceCount);
        pm.hardPieces   = ResolveLevelPieces(hardPf, snapRoot, shared, pm.hardSettings.pieceCount);

        // Count shown in the UI = resolved piece count for each level.
        pm.easySettings.pieceCount   = pm.easyPieces.Count;
        pm.mediumSettings.pieceCount = pm.mediumPieces.Count;
        pm.hardSettings.pieceCount   = pm.hardPieces.Count;

        Debug.Log("[PuzzleSceneBuilder] Per-difficulty robots — " +
                  $"Easy {pm.easyPieces.Count}" + (easyPf != null ? "" : " (fallback)") + ", " +
                  $"Medium {pm.mediumPieces.Count}" + (medPf != null ? "" : " (fallback)") + ", " +
                  $"Hard {pm.hardPieces.Count}" + (hardPf != null ? "" : " (fallback)") + " pieces.");
    }

    /// One level's pieces: its own prefab split in full, or the first-N slice of the shared list.
    private static List<GameObject> ResolveLevelPieces(
        GameObject levelPrefab, Transform snapRoot, List<GameObject> shared, int fallbackCount)
    {
        if (levelPrefab != null)
            return BuildPuzzlePiecesFromPrefab(levelPrefab, snapRoot);

        int n = Mathf.Clamp(fallbackCount, 0, shared != null ? shared.Count : 0);
        return shared != null ? shared.GetRange(0, n) : new List<GameObject>();
    }

    private static List<GameObject> BuildPuzzlePiecesFromPrefab(GameObject prefab, Transform snapRoot)
    {
        // Instantiate + fully unpack so we can split the model into independent scene objects.
        var husk = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        PrefabUtility.UnpackPrefabInstance(husk, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        husk.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        husk.transform.localScale = Vector3.one;

        // Direct children = pieces, ordered by numeric name prefix for the difficulty ramp.
        var children = new List<Transform>();
        foreach (Transform c in husk.transform) children.Add(c);
        children.Sort((a, b) =>
        {
            int oa = PiecePrefixOrder(a.name), ob = PiecePrefixOrder(b.name);
            return oa != ob ? oa.CompareTo(ob) : a.GetSiblingIndex().CompareTo(b.GetSiblingIndex());
        });

        var allPieces = new List<GameObject>();
        foreach (var child in children)
        {
            var pieceGO = MakePuzzlePieceFromObject(child.gameObject, snapRoot);
            pieceGO.SetActive(false);   // inactive until the player selects a difficulty
            allPieces.Add(pieceGO);
        }

        Object.DestroyImmediate(husk);  // empty husk — children were reparented out
        return allPieces;
    }

    /// Leading integer of a piece name ("03_LeftArm" → 3). No prefix → int.MaxValue (sorts last).
    private static int PiecePrefixOrder(string name)
    {
        int i = 0;
        while (i < name.Length && char.IsDigit(name[i])) i++;
        return i > 0 && int.TryParse(name.Substring(0, i), out var n) ? n : int.MaxValue;
    }

    /// "03_LeftArm" → "LeftArm"; "Torso" → "Torso".
    private static string StripPiecePrefix(string name)
    {
        int i = 0;
        while (i < name.Length && char.IsDigit(name[i])) i++;
        if (i > 0 && i < name.Length && (name[i] == '_' || name[i] == '-')) i++;
        return i > 0 && i <= name.Length ? name.Substring(i) : name;
    }

    /// Turn one authored child (kept as-is visually) into a grabbable piece + its ghost/snap zone.
    private static GameObject MakePuzzlePieceFromObject(GameObject piece, Transform snapRoot)
    {
        string name      = StripPiecePrefix(piece.name);
        Vector3 solvedPos = piece.transform.position;
        Quaternion solvedRot = piece.transform.rotation;

        // Detach from the husk → standalone piece keeping its authored world pose.
        piece.transform.SetParent(null, worldPositionStays: true);
        piece.name = $"Piece_Robot_{name}";

        // ── Snap zone + ghost — clone the PRISTINE authored visuals before adding gameplay parts ─
        var snapGO = new GameObject($"SnapZone_Robot_{name}");
        snapGO.transform.SetParent(snapRoot, worldPositionStays: true);
        snapGO.transform.SetPositionAndRotation(solvedPos, solvedRot);
        snapGO.SetActive(false);    // hidden until puzzle starts

        var ghost = Object.Instantiate(piece, snapGO.transform);
        ghost.name = "Ghost";
        ghost.transform.SetPositionAndRotation(solvedPos, solvedRot);
        // Ghost is visual only — strip any authored colliders/bodies.
        foreach (var col in ghost.GetComponentsInChildren<Collider>(true))  Object.DestroyImmediate(col);
        foreach (var bod in ghost.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(bod);

        var ghostIdle   = GetOrCreateMat("Ghost_Idle",   new Color(0.68f, 0.82f, 1.00f, 0.20f), transparent: true);
        var ghostActive = GetOrCreateMat("Ghost_Active", new Color(0.48f, 0.88f, 1.00f, 0.45f), transparent: true);
        var ghostRends  = ghost.GetComponentsInChildren<Renderer>(true);
        foreach (var r in ghostRends)
        {
            r.sharedMaterial     = ghostIdle;
            r.shadowCastingMode  = ShadowCastingMode.Off;
            r.receiveShadows     = false;
        }

        // ── Piece gameplay components ───────────────────────────────────────
        if (piece.GetComponentInChildren<Collider>(true) == null)
        {
            var mf = piece.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                // Convex mesh collider so the authored shape can be grabbed and hits walls.
                var mc = piece.AddComponent<MeshCollider>();
                mc.convex = true;
            }
            else
            {
                // Mesh lives on child objects: approximate with a box over their bounds.
                var rends = piece.GetComponentsInChildren<Renderer>(true);
                if (rends.Length > 0)
                {
                    var b = rends[0].bounds;
                    for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
                    var bc = piece.AddComponent<BoxCollider>();
                    bc.center = piece.transform.InverseTransformPoint(b.center);
                    var ls = piece.transform.lossyScale;
                    bc.size = new Vector3(b.size.x / Mathf.Max(1e-4f, Mathf.Abs(ls.x)),
                                          b.size.y / Mathf.Max(1e-4f, Mathf.Abs(ls.y)),
                                          b.size.z / Mathf.Max(1e-4f, Mathf.Abs(ls.z)));
                }
            }
        }

        var rb = piece.GetComponent<Rigidbody>() ?? piece.AddComponent<Rigidbody>();
        rb.mass          = 0.3f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        var grab = piece.AddComponent<XRGrabInteractable>();
        grab.movementType = XRBaseInteractable.MovementType.VelocityTracking;

        var pp = piece.AddComponent<PuzzlePiece>();
        pp.solveThreshold = 0.05f;
        pp.solvedMaterial = GetOrCreateMat("Piece_Solved", new Color(0.98f, 0.94f, 0.80f));

        // ── Wire the snap zone ──────────────────────────────────────────────
        var msz = snapGO.AddComponent<MagneticSnapZone>();
        msz.linkedPiece         = pp;
        msz.ghostRenderer       = ghostRends.Length > 0 ? ghostRends[0] : null;
        msz.extraGhostRenderers = SubRenderers(ghostRends, 1);
        msz.ghostIdleMaterial   = ghostIdle;
        msz.ghostActiveMaterial = ghostActive;
        msz.activationRange     = 0.15f;

        pp.correctPlacementTarget = snapGO.transform;
        return piece;
    }

    /// Slice of a renderer array from `start` to the end (empty if none) — no LINQ dependency.
    private static Renderer[] SubRenderers(Renderer[] src, int start)
    {
        if (src == null || start >= src.Length) return System.Array.Empty<Renderer>();
        var outArr = new Renderer[src.Length - start];
        System.Array.Copy(src, start, outArr, 0, outArr.Length);
        return outArr;
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
        // Continuous detection so fast moves (or ceiling drops) don't tunnel through thin walls.
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        var grab = go.AddComponent<XRGrabInteractable>();
        // VelocityTracking moves the HELD piece via physics, so it collides with walls, the
        // table and other loose pieces instead of ghosting through them (the default
        // Instantaneous mode teleports the transform and ignores collisions while held).
        grab.movementType = XRBaseInteractable.MovementType.VelocityTracking;

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

        int easyN   = pm.easySettings.pieceCount;
        int mediumN = pm.mediumSettings.pieceCount;
        int hardN   = pm.hardSettings.pieceCount;

        // ── Difficulty panel (single step) ────────────────────────────────
        var diffPanel = MakePanel(root.transform, "DifficultyPanel", panelBg);

        MakeUIText(diffPanel.transform, "Title", "Choose Difficulty",
                   new Vector2(0f, 165f), new Vector2(560f, 60f), 34, Color.white);

        var easyBtn   = MakeUIButton(diffPanel.transform, "EasyBtn",   "Easy",
                                      new Vector2(-190f, 70f), new Color(0.55f, 0.72f, 0.55f));
        var mediumBtn = MakeUIButton(diffPanel.transform, "MediumBtn", "Medium",
                                      new Vector2(   0f, 70f), new Color(0.72f, 0.66f, 0.45f));
        var hardBtn   = MakeUIButton(diffPanel.transform, "HardBtn",   "Hard",
                                      new Vector2( 190f, 70f), new Color(0.72f, 0.48f, 0.48f));

        // Piece-count line directly under each button (the only sub-text on the buttons)
        MakeUIText(diffPanel.transform, "EasyCount",   $"{easyN} pieces",
                   new Vector2(-190f, 22f), new Vector2(155f, 26f), 18, new Color(0.92f, 0.92f, 0.92f));
        MakeUIText(diffPanel.transform, "MediumCount", $"{mediumN} pieces",
                   new Vector2(   0f, 22f), new Vector2(155f, 26f), 18, new Color(0.92f, 0.92f, 0.92f));
        MakeUIText(diffPanel.transform, "HardCount",   $"{hardN} pieces",
                   new Vector2( 190f, 22f), new Vector2(155f, 26f), 18, new Color(0.92f, 0.92f, 0.92f));

        // Round "i" info badge under each difficulty — opens info for THAT mode.
        var easyInfo   = MakeInfoBadge(diffPanel.transform, "EasyInfoBadge",   new Vector2(-190f, -28f));
        var mediumInfo = MakeInfoBadge(diffPanel.transform, "MediumInfoBadge", new Vector2(   0f, -28f));
        var hardInfo   = MakeInfoBadge(diffPanel.transform, "HardInfoBadge",   new Vector2( 190f, -28f));

        MakeUIText(diffPanel.transform, "HintLabel",
                   "Each mode sets your starting assistance — the game then adapts in real time to " +
                   "your stress from the Muse headband. Tap a mode's \"i\" for details.",
                   new Vector2(0f, -110f), new Vector2(560f, 60f), 16, new Color(0.62f, 0.62f, 0.64f));

        // ── Status label (created BEFORE the info overlay so the overlay draws on top of it) ──
        var statusLabel = MakeUIText(root.transform, "StatusLabel", "",
                                     new Vector2(0f, -205f), new Vector2(580f, 44f), 20,
                                     new Color(0.85f, 0.85f, 0.85f));

        // ── Per-mode info overlay panel (one panel; title/body swap per badge) ──
        // Created LAST so it is the top-most sibling: when shown it fully covers the difficulty
        // panel AND the status label, so no underlying text bleeds through the overlay.
        var infoPanel = MakePanel(root.transform, "InfoPanel", new Color(0.06f, 0.06f, 0.08f, 1f));
        var infoTitle = MakeUIText(infoPanel.transform, "InfoTitle", "",
                                   new Vector2(0f, 175f), new Vector2(560f, 55f), 30, Color.white);
        var infoBody  = MakeUIText(infoPanel.transform, "InfoBody", "",
                                   new Vector2(0f, -5f), new Vector2(520f, 300f), 20, new Color(0.86f, 0.86f, 0.88f));
        var closeInfo = MakeUIButton(infoPanel.transform, "InfoCloseBtn", "Close",
                                     new Vector2(0f, -195f), new Color(0.45f, 0.45f, 0.50f));
        infoPanel.SetActive(false);

        // ── Wire DifficultyUI component ────────────────────────────────────
        var diffUI = root.AddComponent<DifficultyUI>();
        diffUI.difficultyPanel   = diffPanel;
        diffUI.easyButton        = easyBtn;
        diffUI.mediumButton      = mediumBtn;
        diffUI.hardButton        = hardBtn;
        diffUI.easyInfoButton    = easyInfo;
        diffUI.mediumInfoButton  = mediumInfo;
        diffUI.hardInfoButton    = hardInfo;
        diffUI.infoCloseButton   = closeInfo;
        diffUI.infoPanel         = infoPanel;
        diffUI.infoTitle         = infoTitle;
        diffUI.infoBody          = infoBody;
        diffUI.statusLabel       = statusLabel;
        diffUI.puzzleManager     = pm;

        // Per-mode info copy (piece counts baked in)
        diffUI.easyInfoTitle   = $"EASY — {easyN} pieces";
        diffUI.easyInfoBody    = "Bright, well-lit room.\n\nPieces start right next to their slots and a " +
                                 "strong magnet pulls each one in — it even snaps home while you're still " +
                                 "holding it. The gentlest mode.";
        diffUI.mediumInfoTitle = $"MEDIUM — {mediumN} pieces";
        diffUI.mediumInfoBody  = "The room goes dark — grab the floating lantern (it clips to your arm) to " +
                                 "search.\n\nPieces are spread further from their slots and the magnet is " +
                                 "lighter, so you place them more deliberately.\n\nIf you get stressed or " +
                                 "stuck on a piece, it and its slot glow the same colour and the lantern " +
                                 "cone widens to help you.";
        diffUI.hardInfoTitle   = $"HARD — {hardN} pieces";
        diffUI.hardInfoBody    = "Dark room filled with walls, columns and baskets.\n\nPieces rain down from " +
                                 "the ceiling and settle among the obstacles. The magnet is weak — this is " +
                                 "mostly hand placement.\n\nWhen you're stressed or stuck: a piece and its " +
                                 "slot glow the same colour, the lantern cone widens, and a long-lost piece " +
                                 "flickers so you can find it.";
    }

    /// Small round "i" badge button (uses the built-in circular Knob sprite).
    private static Button MakeInfoBadge(Transform parent, string name, Vector2 anchoredPos)
    {
        var btn = MakeUIButton(parent, name, "i", anchoredPos, new Color(0.30f, 0.46f, 0.62f));
        var rt  = btn.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(46f, 46f);

        var img  = btn.GetComponent<Image>();
        var knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");
        if (knob != null) { img.sprite = knob; img.type = Image.Type.Simple; }

        // Shrink the label so the lowercase "i" sits centred in the circle.
        var label = btn.GetComponentInChildren<TextMeshProUGUI>();
        if (label != null)
        {
            label.GetComponent<RectTransform>().sizeDelta = new Vector2(40f, 40f);
            label.fontSize  = 26;
            label.fontStyle = FontStyles.Italic | FontStyles.Bold;
        }
        return btn;
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

        // Hover in place until grabbed — with gravity it would fall to the floor and roll away
        // in the pitch-dark room, making it impossible to find. Kinematic keeps it floating and
        // reachable; ArmLantern parents it to the arm on grab anyway.
        var lrb = go.AddComponent<Rigidbody>();
        lrb.mass        = 0.3f;
        lrb.useGravity  = false;
        lrb.isKinematic = true;
        go.AddComponent<XRGrabInteractable>();

        // Glow that travels with the lantern: a real pool of light around the player's hand
        // so grabbing it visibly lights their surroundings (and the lantern is easy to find
        // before grabbing). Reflections are off in dark mode, so it stays a local pool.
        var glowGO = new GameObject("LanternGlow");
        glowGO.transform.SetParent(go.transform, false);
        var glow = glowGO.AddComponent<Light>();
        glow.type      = LightType.Point;
        glow.color     = new Color(1f, 0.85f, 0.55f);
        glow.intensity = 2.4f;
        glow.range     = 3.8f;
        glow.shadows   = LightShadows.None;

        // Beam — OFF until grabbed (ArmLantern enables it). Aims along the lantern's local
        // forward, which becomes the arm direction once clipped to the forearm. Bright and
        // wide so it clearly reveals surfaces it sweeps across.
        var spotGO = new GameObject("LanternSpot");
        spotGO.transform.SetParent(go.transform, false);
        var spot = spotGO.AddComponent<Light>();
        spot.type      = LightType.Spot;
        spot.color     = new Color(1f, 0.93f, 0.75f);
        spot.intensity = 8f;
        spot.range     = 10f;       // shorter throw → less wash on the far walls
        spot.spotAngle = 34f;       // tight, focused cone (was 55 — it spread too much)
        spot.shadows   = LightShadows.None;
        spot.enabled   = false;

        var lantern = go.AddComponent<ArmLantern>();
        lantern.beam          = spot;
        lantern.baseSpotAngle = 34f;   // calm — a focused beam, not a floodlight
        lantern.maxSpotAngle  = 60f;   // very stressed → wider, easier search
        go.SetActive(false);
        return go;
    }

    /// Builds the hard-mode obstacle field: a few low walls, columns and open baskets that
    /// the ceiling-dropped pieces scatter among. Parented under one root that starts inactive;
    /// HardModeDarkroom shows it only on Hard. They sit away from the central table footprint.
    private static GameObject BuildHardObstacles()
    {
        var root = new GameObject("HardObstacles");
        var colMat = GetOrCreateMat("Obstacle_Zen", new Color(0.78f, 0.78f, 0.80f));

        void Add(string n, PrimitiveType prim, Vector3 pos, Vector3 scale, Vector3 euler)
        {
            var go = GameObject.CreatePrimitive(prim);
            go.name = n;
            go.transform.SetParent(root.transform, true);
            go.transform.position   = pos;
            go.transform.localScale = scale;
            go.transform.rotation   = Quaternion.Euler(euler);
            go.GetComponent<Renderer>().material = colMat;
        }

        // Taller walls (4) — on Hard they should block sight-lines so pieces are harder to find
        // (sit each on the floor: centre y = height/2).
        Add("Wall_A", PrimitiveType.Cube, new Vector3(-1.7f, 0.80f,  0.6f), new Vector3(0.15f, 1.6f, 1.6f), Vector3.zero);
        Add("Wall_B", PrimitiveType.Cube, new Vector3( 1.7f, 0.80f, -0.6f), new Vector3(0.15f, 1.6f, 1.6f), Vector3.zero);
        Add("Wall_C", PrimitiveType.Cube, new Vector3( 0.6f, 0.65f,  1.9f), new Vector3(1.8f,  1.3f, 0.15f), Vector3.zero);
        Add("Wall_D", PrimitiveType.Cube, new Vector3(-0.6f, 0.65f, -1.9f), new Vector3(1.8f,  1.3f, 0.15f), Vector3.zero);
        // Columns (3)
        Add("Column_A", PrimitiveType.Cylinder, new Vector3(-1.4f, 0.9f, -1.4f), new Vector3(0.18f, 0.9f, 0.18f), Vector3.zero);
        Add("Column_B", PrimitiveType.Cylinder, new Vector3( 1.4f, 0.9f,  1.4f), new Vector3(0.18f, 0.9f, 0.18f), Vector3.zero);
        Add("Column_C", PrimitiveType.Cylinder, new Vector3( 2.0f, 0.9f,  0.0f), new Vector3(0.18f, 0.9f, 0.18f), Vector3.zero);
        // Baskets — short wide cylinders pieces can fall into (2)
        Add("Basket_A", PrimitiveType.Cylinder, new Vector3(-2.0f, 0.20f, -0.2f), new Vector3(0.55f, 0.20f, 0.55f), Vector3.zero);
        Add("Basket_B", PrimitiveType.Cylinder, new Vector3( 1.0f, 0.20f, -1.5f), new Vector3(0.55f, 0.20f, 0.55f), Vector3.zero);

        root.SetActive(false);
        return root;
    }

    /// Spawns the XR rig at <paramref name="pos"/>. Deliberately minimal — matches the known-good
    /// feature/muse-debug-hud setup: instantiate the prefab and place it, nothing else.
    /// We do NOT rotate the rig, add XRSpawnRecenter, or override the tracking origin:
    ///   • The runtime recenter manipulated the XR Origin/camera and broke head tracking on Quest.
    ///   • Forcing FLOOR tracking buried the player in the floor (Stationary-boundary headsets).
    ///   • The rooms are laid out so the rig's default +Z forward already faces the content.
    /// faceTarget is kept in the signature for call-site compatibility but is intentionally unused.
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
