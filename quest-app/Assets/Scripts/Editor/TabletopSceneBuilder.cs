// Menu: TabletopCraft > Create Scene. Builds the passthrough scene in code (same approach as the HUD repo):
// OVRCameraRig with passthrough, an OVRPassthroughLayer underlay, the MRUK prefab-equivalent component with
// scene loading on startup, and TabletopApp. Fix the builder rather than hand-editing the scene file.
#if UNITY_EDITOR
using Meta.XR.MRUtilityKit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TabletopCraft.EditorTools
{
    public static class TabletopSceneBuilder
    {
        const string ScenePath = "Assets/Scenes/Tabletop.unity";

        [MenuItem("TabletopCraft/Create Scene")]
        public static void Create()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var rigGo = new GameObject("OVRCameraRig");
            var rig = rigGo.AddComponent<OVRCameraRig>();
            var mgr = rigGo.AddComponent<OVRManager>();
            mgr.isInsightPassthroughEnabled = true;
            var pt = rigGo.AddComponent<OVRPassthroughLayer>();
            pt.overlayType = OVROverlay.OverlayType.Underlay;
            rig.EnsureGameObjectIntegrity();
            var eye = rig.centerEyeAnchor.GetComponent<Camera>();
            if (eye != null) { eye.clearFlags = CameraClearFlags.SolidColor; eye.backgroundColor = new Color(0, 0, 0, 0); eye.nearClipPlane = 0.02f; eye.tag = "MainCamera"; }

            var mrukGo = new GameObject("MRUK");
            var mruk = mrukGo.AddComponent<MRUK>();
            // Load the headset's Space Setup scene at startup (fields differ slightly across MRUK versions; check the inspector).
            // SceneSettings is a plain serialized class: null on a component added from code until the inspector fills it.
            mruk.SceneSettings ??= new MRUK.MRUKSettings();
            mruk.SceneSettings.DataSource = MRUK.SceneDataSource.Device;
            mruk.SceneSettings.LoadSceneOnStartup = true;

            var app = new GameObject("TabletopApp").AddComponent<TabletopApp>();
            app.OpaqueMaterial = new Material(Shader.Find("TabletopCraft/Blocks"));
            app.TransparentMaterial = new Material(Shader.Find("TabletopCraft/BlocksTransparent"));
            if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
            AssetDatabase.CreateAsset(app.OpaqueMaterial, "Assets/Materials/Blocks.mat");
            AssetDatabase.CreateAsset(app.TransparentMaterial, "Assets/Materials/BlocksTransparent.mat");
            // Referenced from the scene so Unlit/Color ships in the build (CreatePrimitive's Standard material doesn't).
            app.MarkerMaterial = new Material(Shader.Find("Unlit/Color"));
            AssetDatabase.CreateAsset(app.MarkerMaterial, "Assets/Materials/Marker.mat");

            // Meta project config: passthrough + scene required, Quest 3/3S.
            var cfg = OVRProjectConfig.CachedProjectConfig;
            cfg.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;
            cfg.sceneSupport = OVRProjectConfig.FeatureSupport.Required;
            cfg.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
            cfg.systemLoadingScreenBackground = OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough;
            cfg.targetDeviceTypes = new System.Collections.Generic.List<OVRProjectConfig.DeviceType> { OVRProjectConfig.DeviceType.Quest3, OVRProjectConfig.DeviceType.Quest3S };
            // Plain ws:// to the LAN server: Meta's network security config sets cleartextTrafficPermitted="false",
            // which overrides usesCleartextTraffic in Plugins/Android/AndroidManifest.xml.
            cfg.enableNSCConfig = false;
            OVRProjectConfig.CommitProjectConfig(cfg);

            // The docs and tools push tabletop.json / pack.zip to /sdcard/Android/data/com.tabletopcraft.ar/files/.
            PlayerSettings.companyName = "TabletopCraft";
            PlayerSettings.productName = "TabletopCraft AR";
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.tabletopcraft.ar");
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed; // same for Unity's own HTTP stack

            System.IO.Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[Tabletop] scene created at " + ScenePath);
        }
    }
}
#endif
