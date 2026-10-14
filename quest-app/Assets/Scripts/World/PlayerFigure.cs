// One Minecraft-style player figure on the table: six boxed parts (PlayerModel geometry, shared meshes) under a root
// at the player's feet, skinned with a 64x64 skin through a MaterialPropertyBlock, posed from PlayerAnimation, with
// a name label. Built in figure space (faces +Z, right hand at +X, 1 unit = 1 model pixel); the root scale turns
// pixels into table metres.
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class PlayerFigure
    {
        public readonly GameObject Root;
        public readonly TextMesh Label;
        public bool Slim { get; private set; }
        public Texture2D Skin { get; private set; }

        readonly Transform[] _parts = new Transform[PlayerModel.PartCount];
        readonly MeshFilter[] _filters = new MeshFilter[PlayerModel.PartCount];
        readonly MeshRenderer[] _renderers = new MeshRenderer[PlayerModel.PartCount];
        readonly Vector3[] _pivots = new Vector3[PlayerModel.PartCount];
        readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        static readonly int MainTex = Shader.PropertyToID("_MainTex");

        // Part meshes are the same for every player of a model type: build them once.
        static readonly Dictionary<(PlayerPart, bool), Mesh> MeshCache = new Dictionary<(PlayerPart, bool), Mesh>();

        public PlayerFigure(Transform parent, string name, Material material, Texture2D skin, bool slim)
        {
            Root = new GameObject("player " + name);
            Root.transform.SetParent(parent, false);
            foreach (var def in PlayerModel.Parts(slim))
            {
                int i = (int)def.Part;
                var go = new GameObject(def.Part.ToString());
                go.transform.SetParent(Root.transform, false);
                PlayerModel.Pivot(def, out float px, out float py, out float pz);
                _pivots[i] = new Vector3(px, py, pz);
                go.transform.localPosition = _pivots[i];
                _parts[i] = go.transform;
                _filters[i] = go.AddComponent<MeshFilter>();
                _renderers[i] = go.AddComponent<MeshRenderer>();
                _renderers[i].sharedMaterial = material;
                _renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _renderers[i].receiveShadows = false;
                _renderers[i].lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                _renderers[i].reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            }
            Slim = !slim; // force the mesh assignment below
            SetSkin(skin, slim);

            var labelGo = new GameObject("label");
            labelGo.transform.SetParent(Root.transform, false);
            labelGo.transform.localPosition = new Vector3(0, 36f, 0);
            Label = labelGo.AddComponent<TextMesh>();
            TabletopApp.SetBuiltinFont(Label);
            // Same size on the table as the old capsule label: 0.09 block per character unit, in model pixels.
            Label.text = name; Label.anchor = TextAnchor.LowerCenter; Label.characterSize = 1.44f; Label.fontSize = 48; Label.color = Color.white;
        }

        /// <summary>Swap the skin texture; slim/wide changes only re-point the two arm meshes.</summary>
        public void SetSkin(Texture2D skin, bool slim)
        {
            if (slim != Slim)
            {
                Slim = slim;
                foreach (var def in PlayerModel.Parts(slim))
                {
                    int i = (int)def.Part;
                    _filters[i].sharedMesh = PartMesh(def, slim);
                    PlayerModel.Pivot(def, out float px, out float py, out float pz);
                    _pivots[i] = new Vector3(px, py, pz);
                }
            }
            if (skin == null || skin == Skin) return;
            Skin = skin;
            _mpb.SetTexture(MainTex, skin);
            foreach (var r in _renderers) r.SetPropertyBlock(_mpb);
        }

        static Mesh PartMesh(PartDef def, bool slim)
        {
            bool armVariant = def.Part == PlayerPart.RightArm || def.Part == PlayerPart.LeftArm;
            var key = (def.Part, armVariant && slim);
            if (MeshCache.TryGetValue(key, out var mesh) && mesh != null) return mesh;
            var fm = new FigureMesh();
            PlayerModel.BuildPart(def, fm);
            int n = fm.VertexCount;
            var pos = new Vector3[n]; var nrm = new Vector3[n]; var uv = new Vector2[n];
            for (int k = 0; k < n; k++)
            {
                pos[k] = new Vector3(fm.Pos[3 * k], fm.Pos[3 * k + 1], fm.Pos[3 * k + 2]);
                nrm[k] = new Vector3(fm.Normal[3 * k], fm.Normal[3 * k + 1], fm.Normal[3 * k + 2]);
                uv[k] = new Vector2(fm.Uv[2 * k], fm.Uv[2 * k + 1]);
            }
            mesh = new Mesh { name = $"player {def.Part}{(key.Item2 ? " slim" : "")}" };
            mesh.SetVertices(pos); mesh.SetNormals(nrm); mesh.SetUVs(0, uv);
            mesh.SetTriangles(fm.Tris, 0);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            MeshCache[key] = mesh;
            return mesh;
        }

        /// <summary>Apply a pose. The root's position, yaw and scale are set by the caller.</summary>
        public void Apply(in PlayerPose p)
        {
            var up = new Vector3(0, p.RootDy, 0);
            Set(PlayerPart.Head, up + new Vector3(0, p.HeadDy, 0), Quaternion.Euler(p.HeadPitch, p.HeadYaw, 0));
            Set(PlayerPart.Body, up + new Vector3(0, p.BodyDy, 0), Quaternion.Euler(p.BodyX, 0, 0));
            // The game rotates parts Z * Y * X; arms have no Y rotation here.
            Set(PlayerPart.RightArm, up + new Vector3(0, p.ArmDy, 0), Quaternion.AngleAxis(p.RightArmZ, Vector3.forward) * Quaternion.AngleAxis(p.RightArmX, Vector3.right));
            Set(PlayerPart.LeftArm, up + new Vector3(0, p.ArmDy, 0), Quaternion.AngleAxis(p.LeftArmZ, Vector3.forward) * Quaternion.AngleAxis(p.LeftArmX, Vector3.right));
            Set(PlayerPart.RightLeg, up + new Vector3(0, 0, p.LegDz), Quaternion.Euler(p.RightLegX, 0, 0));
            Set(PlayerPart.LeftLeg, up + new Vector3(0, 0, p.LegDz), Quaternion.Euler(p.LeftLegX, 0, 0));
            Label.transform.localPosition = new Vector3(0, 36f + p.RootDy + p.HeadDy, 0);
        }

        void Set(PlayerPart part, Vector3 offset, Quaternion rot)
        {
            int i = (int)part;
            _parts[i].localPosition = _pivots[i] + offset;
            _parts[i].localRotation = rot;
        }

        public void Destroy() { if (Root != null) Object.Destroy(Root); }
    }
}
