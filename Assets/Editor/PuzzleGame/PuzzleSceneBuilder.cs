using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using TMPro;

/// <summary>
/// Unity Editor tool — builds both puzzle game scenes from scratch.
///
/// USAGE:  In the Unity menu bar click   Puzzle Game  ▶  Build All Scenes
///
/// WHAT IT CREATES:
///   • Assets/Scenes/EntryHall.unity
///       - A 6 × 8 m room with a glowing portal archway.
///       - Walking through the portal loads ZenPuzzleRoom.
///       - Complete XR Origin rig placed 2 m behind the door.
///
///   • Assets/Scenes/ZenPuzzleRoom.unity
///       - An 8 × 8 m room with a central puzzle table.
///       - A 7-piece snowman assembled from Unity primitives.
///       - Easy (3 pieces) / Medium (5) / Hard (7) with magnetic snapping on Easy.
///       - World-space DifficultyUI canvas wired to PuzzleManager.
///       - Snap zone ghosts for every piece, wired to MagneticSnapZone.
///       - Complete XR Origin rig placed 3 m behind the table.
///
///   • Assets/Materials/PuzzleGame/
///       - All required materials (opaque + transparent ghost materials).
///
/// AFTER RUNNING:
///   1. File ▶ Build Settings — verify EntryHall is index 0, ZenPuzzleRoom is index 1.
///   2. Open either scene and press Play to test in the editor.
/// </summary>
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
        Debug.Log("[PuzzleSceneBuilder] All scenes built. Open File > Build Settings and confirm scene order.");
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
    // SCENE: Entry Hall
    // ════════════════════════════════════════════════════════════════════════

    private static void BuildEntryHallScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // ── Lighting ──────────────────────────────────────────────────────
        AddDirectionalLight(new Color(1.0f, 0.95f, 0.85f), 1.0f, Quaternion.Euler(45f, -30f, 0f));
        SetAmbientFlat(new Color(0.20f, 0.20f, 0.25f));

        // ── Materials ─────────────────────────────────────────────────────
        var floorMat  = GetOrCreateMat("Floor_Entry",  new Color(0.45f, 0.45f, 0.50f));
        var wallMat   = GetOrCreateMat("Wall_Entry",   new Color(0.85f, 0.85f, 0.90f));
        var portalMat = GetOrCreateMat("Portal_Frame", new Color(0.20f, 0.60f, 1.00f));

        // ── Room: 6 m wide × 8 m deep × 3 m high ─────────────────────────
        AddFloor(Vector3.zero, 6f, 8f, floorMat);
        AddWalls(6f, 8f, 3f, wallMat);

        // ── Portal archway (two side posts + top beam) ────────────────────
        AddBox("Portal_PostLeft",  new Vector3(-1.0f, 1.5f, 3.8f), new Vector3(0.20f, 3.0f, 0.20f), portalMat);
        AddBox("Portal_PostRight", new Vector3( 1.0f, 1.5f, 3.8f), new Vector3(0.20f, 3.0f, 0.20f), portalMat);
        AddBox("Portal_TopBeam",   new Vector3( 0.0f, 3.1f, 3.8f), new Vector3(2.40f, 0.20f, 0.20f), portalMat);

        // ── Portal trigger (invisible; loads ZenPuzzleRoom on entry) ──────
        var portalGO  = AddBox("ScenePortal", new Vector3(0f, 1.5f, 4.0f), new Vector3(1.8f, 3.0f, 0.4f), null);
        portalGO.GetComponent<Renderer>().enabled = false;         // keep invisible
        var col = portalGO.GetComponent<BoxCollider>();
        col.isTrigger = true;
        var portal = portalGO.AddComponent<ScenePortal>();
        portal.targetSceneName = "ZenPuzzleRoom";
        portal.transitionDelay = 0.5f;

        // ── XR Rig ────────────────────────────────────────────────────────
        SpawnXRRig(new Vector3(0f, 0f, -2f));

        // ── Instructional sign (world-space canvas) ───────────────────────
        AddWorldText("EntranceSign", new Vector3(0f, 2.2f, 1.0f),
                     "Walk through the glowing door\nto begin the puzzle");

        EditorSceneManager.SaveScene(scene, k_EntryScene);
        Debug.Log($"[PuzzleSceneBuilder] Saved {k_EntryScene}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // SCENE: Zen Puzzle Room
    // ════════════════════════════════════════════════════════════════════════

    private static void BuildZenPuzzleRoomScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // ── Lighting (warm, soft) ─────────────────────────────────────────
        AddDirectionalLight(new Color(1.0f, 0.92f, 0.80f), 0.8f, Quaternion.Euler(50f, 20f, 0f));
        SetAmbientFlat(new Color(0.25f, 0.22f, 0.20f));

        // ── Materials ─────────────────────────────────────────────────────
        var floorMat = GetOrCreateMat("Floor_Zen",  new Color(0.60f, 0.55f, 0.45f));
        var wallMat  = GetOrCreateMat("Wall_Zen",   new Color(0.75f, 0.72f, 0.65f));
        var tableMat = GetOrCreateMat("Table_Zen",  new Color(0.35f, 0.25f, 0.15f));

        // ── Room: 8 × 8 × 3 m ────────────────────────────────────────────
        AddFloor(Vector3.zero, 8f, 8f, floorMat);
        AddWalls(8f, 8f, 3f, wallMat);

        // Central puzzle table
        AddBox("PuzzleTable", new Vector3(0f, 0.5f, 0f), new Vector3(1.2f, 1.0f, 1.2f), tableMat);

        // ── XR Rig ────────────────────────────────────────────────────────
        SpawnXRRig(new Vector3(0f, 0f, -3f));

        // ── Puzzle Manager ────────────────────────────────────────────────
        var pmGO = new GameObject("PuzzleManager");
        var pm   = pmGO.AddComponent<PuzzleManager>();
        pm.easyMagnetic   = true;
        pm.mediumMagnetic = false;
        pm.hardMagnetic   = false;

        var anchorGO = new GameObject("PuzzleAnchor");
        anchorGO.transform.position = new Vector3(0f, 1.01f, 0f); // table surface
        pm.puzzleAnchor = anchorGO.transform;

        // ── Snowman figure: 7 pieces made from Unity primitives ───────────
        //   Each row: (display name, primitive, solved world pos, solved euler, local scale, colour)
        var defs = new (string n, PrimitiveType p, Vector3 pos, Vector3 euler, Vector3 scale, Color col)[]
        {
            // ── Easy (first 3) ───────────────────────────────────────────
            ("Body",     PrimitiveType.Sphere,   new Vector3( 0.00f, 1.35f, 0f), Vector3.zero,          new Vector3(0.40f, 0.50f, 0.40f), new Color(0.92f, 0.92f, 0.92f)),
            ("Head",     PrimitiveType.Sphere,   new Vector3( 0.00f, 1.75f, 0f), Vector3.zero,          new Vector3(0.30f, 0.30f, 0.30f), new Color(0.96f, 0.96f, 0.96f)),
            ("Hat",      PrimitiveType.Cylinder, new Vector3( 0.00f, 2.00f, 0f), Vector3.zero,          new Vector3(0.25f, 0.12f, 0.25f), new Color(0.12f, 0.08f, 0.04f)),
            // ── Medium adds arms (first 5) ───────────────────────────────
            ("LeftArm",  PrimitiveType.Cylinder, new Vector3(-0.45f, 1.45f, 0f), new Vector3(0f, 0f,  90f), new Vector3(0.10f, 0.28f, 0.10f), new Color(0.85f, 0.78f, 0.68f)),
            ("RightArm", PrimitiveType.Cylinder, new Vector3( 0.45f, 1.45f, 0f), new Vector3(0f, 0f, -90f), new Vector3(0.10f, 0.28f, 0.10f), new Color(0.85f, 0.78f, 0.68f)),
            // ── Hard adds legs (all 7) ────────────────────────────────────
            ("LeftLeg",  PrimitiveType.Cylinder, new Vector3(-0.14f, 1.00f, 0f), new Vector3(0f, 0f,  10f), new Vector3(0.12f, 0.28f, 0.12f), new Color(0.90f, 0.90f, 0.90f)),
            ("RightLeg", PrimitiveType.Cylinder, new Vector3( 0.14f, 1.00f, 0f), new Vector3(0f, 0f, -10f), new Vector3(0.12f, 0.28f, 0.12f), new Color(0.90f, 0.90f, 0.90f)),
        };

        var snapRoot    = new GameObject("SnapZones");
        pm.easyPieces   = new List<GameObject>();
        pm.mediumPieces = new List<GameObject>();
        pm.hardPieces   = new List<GameObject>();

        for (int i = 0; i < defs.Length; i++)
        {
            var d = defs[i];
            var (pieceGO, _) = MakePuzzlePiece(d.n, d.p, d.pos, d.euler, d.scale, d.col, snapRoot.transform);

            pm.hardPieces.Add(pieceGO);               // all 7
            if (i < 5) pm.mediumPieces.Add(pieceGO);  // first 5
            if (i < 3) pm.easyPieces.Add(pieceGO);    // first 3
        }

        // ── Difficulty UI (world-space canvas, faces the player) ──────────
        BuildDifficultyCanvas(pm, new Vector3(0f, 1.8f, -1.8f));

        // ── Completion banner ─────────────────────────────────────────────
        AddWorldText("CompletionBanner", new Vector3(0f, 2.9f, 0f), "Assemble the snowman!");

        EditorSceneManager.SaveScene(scene, k_ZenScene);
        Debug.Log($"[PuzzleSceneBuilder] Saved {k_ZenScene}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Puzzle piece factory
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates one puzzle piece GameObject and its matching snap zone target.
    /// The piece starts at solvedPos; PuzzleManager.StartPuzzle() will scatter it.
    /// </summary>
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

        // Physics
        var rb = go.AddComponent<Rigidbody>();
        rb.mass          = 0.3f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        // VR grab
        go.AddComponent<XRGrabInteractable>();

        // Game logic
        var pp = go.AddComponent<PuzzlePiece>();
        pp.magnetForce    = 5f;
        pp.magnetRange    = 0.15f;
        pp.solveThreshold = 0.05f;
        pp.solvedMaterial = GetOrCreateMat("Piece_Solved", new Color(0.30f, 1.00f, 0.40f));

        // ── Snap zone target ──────────────────────────────────────────────
        var snapGO = new GameObject($"SnapZone_{pieceName}");
        snapGO.transform.SetParent(snapRoot, worldPositionStays: true);
        snapGO.transform.position = solvedPos;
        snapGO.transform.rotation = Quaternion.Euler(solvedEuler);

        // Ghost mesh: semi-transparent duplicate of the piece
        var ghost = GameObject.CreatePrimitive(prim);
        ghost.name = "Ghost";
        ghost.transform.SetParent(snapGO.transform, worldPositionStays: false);
        ghost.transform.localPosition = Vector3.zero;
        ghost.transform.localRotation = Quaternion.identity;
        ghost.transform.localScale    = scale;
        Object.DestroyImmediate(ghost.GetComponent<Collider>());    // ghosts don't need physics

        var ghostRend = ghost.GetComponent<Renderer>();
        ghostRend.shadowCastingMode = ShadowCastingMode.Off;
        ghostRend.receiveShadows    = false;

        // MagneticSnapZone component wired to this piece
        var msz = snapGO.AddComponent<MagneticSnapZone>();
        msz.linkedPiece         = pp;
        msz.ghostRenderer       = ghostRend;
        msz.ghostIdleMaterial   = GetOrCreateMat("Ghost_Idle",   new Color(0.60f, 0.80f, 1.0f, 0.25f), transparent: true);
        msz.ghostActiveMaterial = GetOrCreateMat("Ghost_Active", new Color(0.40f, 0.90f, 1.0f, 0.50f), transparent: true);
        msz.activationRange     = 0.15f;

        // Cross-wire: piece knows where it should land
        pp.correctPlacementTarget = snapGO.transform;

        return (go, snapGO);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Difficulty UI canvas builder
    // ════════════════════════════════════════════════════════════════════════

    private static void BuildDifficultyCanvas(PuzzleManager pm, Vector3 worldPos)
    {
        var root = new GameObject("DifficultyUI_Canvas");
        root.transform.position = worldPos;
        root.transform.rotation = Quaternion.identity;

        var canvas       = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.AddComponent<CanvasScaler>();
        root.AddComponent<GraphicRaycaster>();

        var rootRT       = root.GetComponent<RectTransform>();
        rootRT.sizeDelta  = new Vector2(600f, 400f);
        rootRT.localScale = Vector3.one * 0.003f;   // 600 px canvas ≈ 1.8 m wide in world space

        // ── Background panel ──────────────────────────────────────────────
        var panel = new GameObject("QuestionnairePanel");
        panel.transform.SetParent(root.transform, false);
        var panelRT = panel.AddComponent<RectTransform>();
        panelRT.anchorMin = Vector2.zero;
        panelRT.anchorMax = Vector2.one;
        panelRT.offsetMin = panelRT.offsetMax = Vector2.zero;
        panel.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.12f, 0.88f);

        // ── Title ─────────────────────────────────────────────────────────
        MakeUIText(panel.transform, "Title", "Choose Difficulty",
                   new Vector2(0f, 130f), new Vector2(550f, 60f), 36, Color.white);

        // ── Difficulty buttons ────────────────────────────────────────────
        var easyBtn   = MakeUIButton(panel.transform, "EasyBtn",   "Easy",
                                     new Vector2(-180f, 0f), new Color(0.30f, 0.80f, 0.30f));
        var mediumBtn = MakeUIButton(panel.transform, "MediumBtn", "Medium",
                                     new Vector2(   0f, 0f), new Color(0.90f, 0.75f, 0.20f));
        var hardBtn   = MakeUIButton(panel.transform, "HardBtn",   "Hard",
                                     new Vector2( 180f, 0f), new Color(0.85f, 0.25f, 0.25f));

        // ── Feedback label (DifficultyUI updates this at runtime) ─────────
        var selectedLabel = MakeUIText(panel.transform, "SelectedLabel", "",
                                       new Vector2(0f, -110f), new Vector2(550f, 50f), 22,
                                       new Color(0.9f, 0.9f, 0.9f));

        // ── Wire up DifficultyUI ──────────────────────────────────────────
        var diffUI = root.AddComponent<DifficultyUI>();
        diffUI.easyButton              = easyBtn;
        diffUI.mediumButton            = mediumBtn;
        diffUI.hardButton              = hardBtn;
        diffUI.questionnairePanel      = panel;
        diffUI.selectedDifficultyLabel = selectedLabel;
        diffUI.puzzleManager           = pm;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Environment helpers
    // ════════════════════════════════════════════════════════════════════════

    private static void AddDirectionalLight(Color color, float intensity, Quaternion rotation)
    {
        var go    = new GameObject("Directional Light");
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

    /// <param name="roomW">Full width in metres.</param>
    /// <param name="roomD">Full depth in metres.</param>
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
            Debug.LogWarning($"[PuzzleSceneBuilder] XR Rig prefab not found at:\n  {k_XRRigPrefab}\n  → Add 'Complete XR Origin Set Up Variant' manually.");
        }
    }

    private static void AddWorldText(string goName, Vector3 pos, string text)
    {
        var go    = new GameObject(goName);
        go.transform.position = pos;
        var c     = go.AddComponent<Canvas>();
        c.renderMode = RenderMode.WorldSpace;
        go.AddComponent<CanvasScaler>();
        var rt    = go.GetComponent<RectTransform>();
        rt.sizeDelta  = new Vector2(600f, 120f);
        rt.localScale = Vector3.one * 0.003f;
        MakeUIText(go.transform, "Label", text, Vector2.zero, new Vector2(580f, 110f), 28, Color.white);
    }

    // ════════════════════════════════════════════════════════════════════════
    // UI helpers
    // ════════════════════════════════════════════════════════════════════════

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
        rt.sizeDelta        = new Vector2(160f, 70f);
        rt.anchoredPosition = anchoredPos;
        var img = go.AddComponent<Image>();
        img.color = bgColor;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        MakeUIText(go.transform, "Label", label, Vector2.zero, new Vector2(150f, 60f), 24, Color.white);
        return btn;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Material helper
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Loads an existing material asset, or creates and saves a new one.</summary>
    private static Material GetOrCreateMat(string matName, Color color, bool transparent = false)
    {
        var path = $"{k_MatDir}/{matName}.mat";
        var mat  = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;

        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        mat        = new Material(shader) { name = matName };

        if (transparent)
        {
            // URP transparent surface type
            mat.SetFloat("_Surface",   1f);   // 0 = Opaque, 1 = Transparent
            mat.SetFloat("_Blend",     0f);   // Alpha blend
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite",   0);
            mat.renderQueue = 3000;           // RenderQueue.Transparent
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
        }

        mat.color = color;
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Build Settings helper
    // ════════════════════════════════════════════════════════════════════════

    private static void AddScenesToBuildSettings(params string[] scenePaths)
    {
        var existing = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        foreach (var path in scenePaths)
        {
            if (!existing.Exists(s => s.path == path))
                existing.Add(new EditorBuildSettingsScene(path, true));
        }
        EditorBuildSettings.scenes = existing.ToArray();
    }

    // ════════════════════════════════════════════════════════════════════════
    // Folder utility
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Recursively ensures every folder in the path exists as a Unity asset folder.</summary>
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
