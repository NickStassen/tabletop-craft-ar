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

        public static void BuildApk()
        {
            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[Tabletop] Build {report.summary.result}: {Path.GetFullPath(ApkPath)}, " +
                $"{report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalErrors} errors, {report.summary.totalTime}");
            if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }

        /// <summary>Same as ticking OpenXR in Project Settings > XR Plug-in Management for the given platform.</summary>
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
