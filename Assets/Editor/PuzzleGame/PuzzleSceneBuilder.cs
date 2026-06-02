using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
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
    private const string k_MatDir      = "Assets/Materials/PuzzleGame";
    private const string k_EntryScene  = "Assets/Scenes/EntryHall.unity";
    private const string k_ZenScene    = "Assets/Scenes/ZenPuzzleRoom.unity";
    private const string k_XRRigPrefab = "Assets/VRTemplateAssets/Prefabs/Setup/Complete XR Origin Set Up Variant.prefab";

    // ── Menu items ───────────────────────────────────────────────────────────

    [MenuItem("Puzzle Game/Build All Scenes")]
    public static void BuildAll()
    {
        EnsureFolderPath(k_MatDir);
        BuildEntryHallScene();
        BuildZenPuzzleRoomScene();
        AddScenesToBuildSettings(k_EntryScene, k_ZenScene);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[PuzzleSceneBuilder] All scenes built. Check File > Build Settings for scene order.");
    }

    [MenuItem("Puzzle Game/Build Entry Hall Only")]
    public static void BuildEntryOnly()
    {
        EnsureFolderPath(k_MatDir);
        BuildEntryHallScene();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    [MenuItem("Puzzle Game/Build Zen Puzzle Room Only")]
    public static void BuildZenOnly()
    {
        EnsureFolderPath(k_MatDir);
        BuildZenPuzzleRoomScene();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
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

        SpawnXRRig(new Vector3(0f, 0f, -2f));
        AddWorldText("EntranceSign", new Vector3(0f, 2.2f, 1.0f),
                     "Walk through the archway\nto begin the puzzle");

        EditorSceneManager.SaveScene(scene, k_EntryScene);
        Debug.Log($"[PuzzleSceneBuilder] Saved {k_EntryScene}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // SCENE: Zen Puzzle Room
    // ════════════════════════════════════════════════════════════════════════

    private static void BuildZenPuzzleRoomScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Soft neutral lighting — no warm tones, keeps grey surfaces truly grey
        AddDirectionalLight(new Color(0.95f, 0.95f, 0.92f), 0.55f, Quaternion.Euler(55f, 25f, 0f));
        SetAmbientFlat(new Color(0.32f, 0.32f, 0.32f));

        // Zen grey materials
        var floorMat = GetOrCreateMat("Floor_Zen",  new Color(0.82f, 0.82f, 0.82f));
        var wallMat  = GetOrCreateMat("Wall_Zen",   new Color(0.87f, 0.87f, 0.87f));
        var tableMat = GetOrCreateMat("Table_Zen",  new Color(0.93f, 0.93f, 0.93f));

        // 8 × 8 × 3 m room
        AddFloor(Vector3.zero, 8f, 8f, floorMat);
        AddWalls(8f, 8f, 3f, wallMat);

        // Central puzzle table  (top surface at y = 1.0)
        AddBox("PuzzleTable", new Vector3(0f, 0.5f, 0f), new Vector3(1.4f, 1.0f, 1.4f), tableMat);

        SpawnXRRig(new Vector3(0f, 0f, -3f));

        // ── Systems ───────────────────────────────────────────────────────
        var colaGO = new GameObject("CognitiveLoadAdapter");
        colaGO.AddComponent<CognitiveLoadAdapter>();

        var phsGO = new GameObject("PieceHintSystem");
        var phs   = phsGO.AddComponent<PieceHintSystem>();

        // ── Puzzle Manager ────────────────────────────────────────────────
        var pmGO = new GameObject("PuzzleManager");
        var pm   = pmGO.AddComponent<PuzzleManager>();
        pm.hintSystem = phs;

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
        root.AddComponent<GraphicRaycaster>();

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
                                      new Vector2(-190f, 40f), new Color(0.72f, 0.72f, 0.72f));
        var mediumBtn = MakeUIButton(diffPanel.transform, "MediumBtn", "Medium",
                                      new Vector2(   0f, 40f), new Color(0.60f, 0.60f, 0.62f));
        var hardBtn   = MakeUIButton(diffPanel.transform, "HardBtn",   "Hard",
                                      new Vector2( 190f, 40f), new Color(0.48f, 0.48f, 0.50f));

        MakeUIText(diffPanel.transform, "HintLabel",
                   "Easy: guided    Medium: subtle    Hard: on your own",
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

    private static void SpawnXRRig(Vector3 pos)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(k_XRRigPrefab);
        if (prefab != null)
        {
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            rig.transform.position = pos;
        }
        else
        {
            Debug.LogWarning($"[PuzzleSceneBuilder] XR Rig prefab not found:\n  {k_XRRigPrefab}");
        }
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
