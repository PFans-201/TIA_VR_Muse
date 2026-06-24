using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// Command-line Android build entry point, for headless batch builds:
///
///   Unity -batchmode -quit -projectPath . -buildTarget Android \
///         -executeMethod BuildScript.BuildAndroid -logFile build.log
///
/// Output path is taken from the TIA_BUILD_OUT env var (a directory), else ./Builds.
/// Exits with code 0 on success, non-zero on failure, so a CI/shell caller can detect it.
public static class BuildScript
{
    public static void BuildAndroid()
    {
        string outDir = System.Environment.GetEnvironmentVariable("TIA_BUILD_OUT");
        if (string.IsNullOrEmpty(outDir))
            outDir = Path.Combine(Directory.GetCurrentDirectory(), "Builds");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, "TIA_VR_Muse.apk");

        string[] scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            Debug.LogError("[BuildScript] No enabled scenes in Build Settings — aborting.");
            EditorApplication.Exit(2);
            return;
        }

        var opts = new BuildPlayerOptions
        {
            scenes           = scenes,
            locationPathName = outPath,
            target           = BuildTarget.Android,
            targetGroup      = BuildTargetGroup.Android,
            options          = BuildOptions.None,
        };

        Debug.Log($"[BuildScript] Building Android APK -> {outPath}\nScenes:\n  " +
                  string.Join("\n  ", scenes));

        BuildReport report = BuildPipeline.BuildPlayer(opts);
        BuildSummary s = report.summary;

        Debug.Log($"[BuildScript] Result={s.result}  size={s.totalSize} bytes  " +
                  $"time={s.totalTime}  errors={s.totalErrors}  warnings={s.totalWarnings}  out={s.outputPath}");

        EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
    }
}
