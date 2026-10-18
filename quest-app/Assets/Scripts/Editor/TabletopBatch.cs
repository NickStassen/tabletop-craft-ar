// Headless versions of the Editor setup steps, for Linux (no Link / Play mode) and scripted builds. Close the Editor first.
//   Unity -batchmode -nographics -projectPath quest-app -buildTarget Android -executeMethod TabletopCraft.EditorTools.TabletopBatch.Setup -logFile setup.log
//   Unity -batchmode -nographics -quit -projectPath quest-app -buildTarget Android -executeMethod TabletopCraft.EditorTools.TabletopBatch.BuildApk -logFile build.log
//   (BuildReleaseApk: the same, but refuses to build without the release key; docs/RELEASING.md)
// Run Setup WITHOUT -quit: Meta's Project Setup Tool fixes run on editor updates, and Setup exits once they're done.
// Same approach as ironman-quest3-hud's IronManHudBatch.
#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
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
        /// Builds the APK, release-signed when the TABLETOP_KEYSTORE variables are set (<see cref="ReleaseKey"/>), else
        /// with Unity's debug key (fine for sideloading, never for a store upload). A build that failed only because a
        /// build tool crashed with a segmentation fault is run again, up to <see cref="MaxBuildAttempts"/> builds in all:
        /// on Linux, il2cpp's bundled .NET runtime now and then crashes in <c>il2cpp --convert-to-cpp</c> and passes when
        /// run again (QUEST_SETUP.md Troubleshooting, TESTING.md section 5). Any other failure ends the build at once. Bee
        /// is incremental, so a retry only redoes the failed step and what follows it.
        /// </summary>
        public static void BuildApk() => Build(requireRelease: false);

        /// <summary>BuildApk for a store upload: fails unless all four TABLETOP_KEYSTORE variables are set.</summary>
        public static void BuildReleaseApk() => Build(requireRelease: true);

        static void Build(bool requireRelease)
        {
            var key = ReleaseKey.FromEnvironment(out string problem);
            if (problem == null && key == null && requireRelease)
                problem = "BuildReleaseApk needs the release key: set " + string.Join(", ", ReleaseKey.Variables) + " (docs/RELEASING.md)";
            if (problem != null) { Debug.LogError("[Tabletop] " + problem); EditorApplication.Exit(1); return; }
            ApplyVersion();
            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,   // never Development: a store build must not be debuggable
            };
            BuildReport report = null;
            try
            {
                UseKey(key);
                Active = key;   // ReapplyReleaseKey sets it again after the other build steps
                for (int attempt = 1; attempt <= MaxBuildAttempts; attempt++)
                {
                    report = BuildPipeline.BuildPlayer(options);
                    Debug.Log($"[Tabletop] Build {report.summary.result}: {Path.GetFullPath(ApkPath)}, " +
                        $"{report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalErrors} errors, {report.summary.totalTime}, " +
                        (key != null ? "release-signed" : "debug-signed") + (attempt > 1 ? $" (attempt {attempt} of {MaxBuildAttempts})" : ""));
                    if (report.summary.result != BuildResult.Succeeded) LogSigning("after the failed build");
                    if (report.summary.result == BuildResult.Succeeded || !OnlyToolCrashes(report)) break;
                    if (attempt < MaxBuildAttempts)
                        Debug.LogWarning("[Tabletop] A build tool crashed (Segmentation fault; on Linux usually il2cpp's own .NET runtime, " +
                            $"not our code). Building again, attempt {attempt + 1} of {MaxBuildAttempts}. See QUEST_SETUP.md Troubleshooting.");
                }
            }
            finally { Active = null; UseKey(null); }   // ProjectSettings.asset never records the keystore path or alias (locally)
            if (report == null || report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }

        /// <summary>
        /// The release key from the environment (docs/RELEASING.md): TABLETOP_KEYSTORE (path to the .jks, outside the
        /// repo), TABLETOP_KEYSTORE_PASS, TABLETOP_KEY_ALIAS, TABLETOP_KEY_PASS. None set: null (debug key). Some set, or
        /// a keystore that isn't a file: a problem, so a typo never turns into a debug-signed store build. The values are
        /// never logged or written to a file.
        /// </summary>
        internal sealed class ReleaseKey
        {
            public static readonly string[] Variables = { "TABLETOP_KEYSTORE", "TABLETOP_KEYSTORE_PASS", "TABLETOP_KEY_ALIAS", "TABLETOP_KEY_PASS" };
            public string Keystore, KeystorePass, Alias, KeyPass;

            public static ReleaseKey FromEnvironment(out string problem)
            {
                problem = null;
                var v = Variables.Select(n => System.Environment.GetEnvironmentVariable(n) ?? "").ToArray();
                var missing = Variables.Where((n, i) => v[i].Length == 0).ToArray();
                if (missing.Length == Variables.Length)
                {
                    Debug.Log("[Tabletop] TABLETOP_KEYSTORE is not set: debug signing (fine for sideloading; a store upload needs " +
                        "BuildReleaseApk with the release key, docs/RELEASING.md)");
                    return null;
                }
                if (missing.Length > 0) { problem = "release signing: " + string.Join(", ", missing) + " not set (set all four or none; docs/RELEASING.md)"; return null; }
                string path = Path.GetFullPath(v[0].StartsWith("~/") ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), v[0].Substring(2)) : v[0]);
                if (!File.Exists(path)) { problem = "release signing: TABLETOP_KEYSTORE is not a file"; return null; }
                return new ReleaseKey { Keystore = path, KeystorePass = v[1], Alias = v[2], KeyPass = v[3] };
            }
        }

        /// <summary>The release key of the build in progress (null: none, or a debug build).</summary>
        internal static ReleaseKey Active;

        /// <summary>Sets Unity's Android signing to the key (null = the debug key and every field cleared).</summary>
        internal static void UseKey(ReleaseKey key)
        {
            // Only what changes is set: assigning an empty keystore name records "{inproject}: " in ProjectSettings.asset.
            bool use = key != null;
            string store = key?.Keystore ?? "", alias = key?.Alias ?? "";
            if (PlayerSettings.Android.useCustomKeystore != use) PlayerSettings.Android.useCustomKeystore = use;
            if (Blank(PlayerSettings.Android.keystoreName) != Blank(store) || (!Blank(store) && PlayerSettings.Android.keystoreName != store))
                PlayerSettings.Android.keystoreName = store;
            if ((PlayerSettings.Android.keyaliasName ?? "") != alias) PlayerSettings.Android.keyaliasName = alias;
            PlayerSettings.Android.keystorePass = key?.KeystorePass ?? "";   // passwords are never written to ProjectSettings.asset
            PlayerSettings.Android.keyaliasPass = key?.KeyPass ?? "";
            if (key != null) Debug.Log($"[Tabletop] release signing with {Path.GetFileName(key.Keystore)}, alias {key.Alias}");
            // [ci] On a cold project (CI), the build's own steps (the Meta SDK's) make Unity reimport ProjectSettings.asset
            // from disk mid-build, dropping a keystore name that was only set in memory ("keystore file not found"). In CI
            // the keystore path and alias are saved to the throwaway checkout's ProjectSettings.asset (never the passwords:
            // Unity doesn't serialize them); locally they stay in memory so the repo's file never records a local path.
            if (key != null && Ci) { AssetDatabase.SaveAssets(); LogSigning("key set"); }
        }

        static bool Ci => System.Environment.GetEnvironmentVariable("TABLETOP_CI") == "1";

        /// <summary>[ci] What Unity will sign with: the keystore path and whether it exists, never the passwords.</summary>
        internal static void LogSigning(string when)
        {
            if (!Ci) return;
            string name = PlayerSettings.Android.keystoreName ?? "";
            Debug.Log($"[Tabletop] signing ({when}): custom keystore {PlayerSettings.Android.useCustomKeystore}, keystore '{name}' " +
                $"(exists {File.Exists(name)}), alias set {!string.IsNullOrEmpty(PlayerSettings.Android.keyaliasName)}, " +
                $"passwords set {!string.IsNullOrEmpty(PlayerSettings.Android.keystorePass)}/{!string.IsNullOrEmpty(PlayerSettings.Android.keyaliasPass)}");
        }

        /// <summary>An unset keystore name: empty, or Unity's "{inproject}: " with nothing after it.</summary>
        static bool Blank(string keystoreName)
        {
            string s = (keystoreName ?? "").Trim();
            return s.Length == 0 || s == "{inproject}:";
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

        /// <summary>
        /// App version from the repo's VERSION file (shared with the plugin): bundleVersion "M.m.p" and the Android
        /// versionCode M*1000000 + m*10000 + p*100 + TABLETOP_BUILD (0-99, default 0: a re-upload of the same version to
        /// a store channel needs a higher code; docs/RELEASING.md).
        /// </summary>
        static void ApplyVersion()
        {
            string v = File.ReadAllText(Path.Combine("..", "VERSION")).Trim();
            var parts = v.Split('.').Select(int.Parse).ToArray();
            string b = System.Environment.GetEnvironmentVariable("TABLETOP_BUILD");
            int build = int.TryParse(b, out int n) && n >= 0 && n <= 99 ? n : 0;
            if (!string.IsNullOrEmpty(b) && build.ToString() != b.Trim()) Debug.LogWarning("[Tabletop] TABLETOP_BUILD must be 0..99, ignored: " + b);
            PlayerSettings.bundleVersion = v;
            PlayerSettings.Android.bundleVersionCode = parts[0] * 1000000 + parts[1] * 10000 + (parts.Length > 2 ? parts[2] : 0) * 100 + build;
            AssetDatabase.SaveAssets();
            Debug.Log($"[Tabletop] version {v} (code {PlayerSettings.Android.bundleVersionCode})");
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

    /// <summary>[ci] Runs after every other build step and sets the release key again (see TabletopBatch.UseKey).</summary>
    sealed class ReapplyReleaseKey : IPreprocessBuildWithReport
    {
        public int callbackOrder => int.MaxValue;
        public void OnPreprocessBuild(BuildReport report)
        {
            if (TabletopBatch.Active == null) return;
            TabletopBatch.LogSigning("last build step, before");
            TabletopBatch.UseKey(TabletopBatch.Active);
        }
    }
}
#endif
