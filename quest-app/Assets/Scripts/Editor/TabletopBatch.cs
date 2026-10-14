// Headless versions of the Editor setup steps, for Linux (no Link / Play mode) and scripted builds. Close the Editor first.
//   Unity -batchmode -nographics -projectPath quest-app -buildTarget Android -executeMethod TabletopCraft.EditorTools.TabletopBatch.Setup -logFile setup.log
//   Unity -batchmode -nographics -quit -projectPath quest-app -buildTarget Android -executeMethod TabletopCraft.EditorTools.TabletopBatch.BuildApk -logFile build.log
// Run Setup WITHOUT -quit: Meta's Project Setup Tool fixes run on editor updates, and Setup exits once they're done.
// Same approach as ironman-quest3-hud's IronManHudBatch.
#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.Management;

namespace TabletopCraft.EditorTools
{
    public static class TabletopBatch
    {
        const string OpenXrLoader = "UnityEngine.XR.OpenXR.OpenXRLoader";
        const string ApkPath = "Builds/TabletopCraftAR.apk";

        /// <summary>OpenXR loader on Android, then TabletopCraft > Create Scene, then Project Setup Tool > Fix All.</summary>
        public static void Setup()
        {
            EnableOpenXrLoader(BuildTargetGroup.Android);
            TabletopSceneBuilder.Create();

            var fix = OVRProjectSetup.FixAllAsync(BuildTargetGroup.Android);
            void WaitForFix()
            {
                if (!fix.IsCompleted) return;
                EditorApplication.update -= WaitForFix;
                if (fix.IsFaulted) Debug.LogException(fix.Exception);
                // Some fixes edit OVRManager in the open scene (Scene permission request, dynamic resolution): save it.
                UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
                AssetDatabase.SaveAssets();
                Debug.Log("[Tabletop] Batch setup done. Run it again until nothing changes, then BuildApk.");
                EditorApplication.Exit(fix.IsFaulted ? 1 : 0);
            }
            EditorApplication.update += WaitForFix;
        }

        /// <summary>
        /// Builds the APK. A build that failed only because a build tool crashed with a segmentation fault is run again,
        /// up to <see cref="MaxBuildAttempts"/> builds in all: on Linux, il2cpp's bundled .NET runtime now and then
        /// crashes in <c>il2cpp --convert-to-cpp</c> and passes when run again (QUEST_SETUP.md Troubleshooting,
        /// TESTING.md section 5). Any other failure ends the build at once. Bee is incremental, so a retry only redoes
        /// the failed step and what follows it.
        /// </summary>
        public static void BuildApk()
        {
            ApplyVersion();
            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };
            BuildReport report = null;
            for (int attempt = 1; attempt <= MaxBuildAttempts; attempt++)
            {
                report = BuildPipeline.BuildPlayer(options);
                Debug.Log($"[Tabletop] Build {report.summary.result}: {Path.GetFullPath(ApkPath)}, " +
                    $"{report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalErrors} errors, {report.summary.totalTime}" +
                    (attempt > 1 ? $" (attempt {attempt} of {MaxBuildAttempts})" : ""));
                if (report.summary.result == BuildResult.Succeeded || !OnlyToolCrashes(report)) break;
                if (attempt < MaxBuildAttempts)
                    Debug.LogWarning("[Tabletop] A build tool crashed (Segmentation fault; on Linux usually il2cpp's own .NET runtime, " +
                        $"not our code). Building again, attempt {attempt + 1} of {MaxBuildAttempts}. See QUEST_SETUP.md Troubleshooting.");
            }
            if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }

        const int MaxBuildAttempts = 3;

        /// <summary>
        /// Every error of the failed build is a tool's "Segmentation fault (core dumped)". That line is all the report
        /// has: Bee prints the crashed command line (il2cpp) to stdout, and only the tool's output becomes the error.
        /// </summary>
        static bool OnlyToolCrashes(BuildReport report)
        {
            var errors = report.steps.SelectMany(step => step.messages)
                .Where(m => m.type == LogType.Error || m.type == LogType.Exception).Select(m => m.content).ToList();
            if (errors.Count == 0) errors.Add(report.SummarizeErrors());
            return errors.All(e => e != null && (e.Contains("Segmentation fault") || e.Contains("core dumped")));
        }

        /// <summary>Same as ticking OpenXR in Project Settings > XR Plug-in Management for the given platform.</summary>
        /// <summary>App version from the repo's VERSION file (shared with the plugin): bundleVersion "0.X.Y" and a
        /// monotonically increasing versionCode X*10000 + Y*100 for Android.</summary>
        static void ApplyVersion()
        {
            string v = File.ReadAllText(Path.Combine("..", "VERSION")).Trim();
            var parts = v.Split('.').Select(int.Parse).ToArray();
            PlayerSettings.bundleVersion = v;
            PlayerSettings.Android.bundleVersionCode = parts[0] * 1000000 + parts[1] * 10000 + (parts.Length > 2 ? parts[2] : 0) * 100;
            AssetDatabase.SaveAssets();
            Debug.Log($"[Tabletop] version {v} (code {PlayerSettings.Android.bundleVersionCode})");
        }

        static void EnableOpenXrLoader(BuildTargetGroup group)
        {
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
            {
                Directory.CreateDirectory("Assets/XR");
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            }
            if (!perTarget.HasManagerSettingsForBuildTarget(group)) perTarget.CreateDefaultManagerSettingsForBuildTarget(group);
            var settings = perTarget.SettingsForBuildTarget(group);
            settings.InitManagerOnStart = true;
            if (!settings.Manager.activeLoaders.Any(l => l != null && l.GetType().FullName == OpenXrLoader))
                XRPackageMetadataStore.AssignLoader(settings.Manager, OpenXrLoader, group);
            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(perTarget);
            AssetDatabase.SaveAssets();
        }
    }
}
#endif
