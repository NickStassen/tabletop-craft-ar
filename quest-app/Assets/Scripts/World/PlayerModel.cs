// Minecraft player model geometry (the game's PlayerModel / HumanoidModel boxes and ModelPart.Cube UV layout),
// built for the Quest's figure space: origin between the feet, +Y up, the figure faces +Z and its right hand is
// at +X (Unity's left-handed frame, so the figure is not mirrored). Units are model pixels (1/16 block). Engine-free
// (plain float lists) so Tests~ can check it; PlayerFigure turns it into Unity meshes.
using System.Collections.Generic;

namespace TabletopCraft.World
{
    public enum PlayerPart { Head, Body, RightArm, LeftArm, RightLeg, LeftLeg }

    /// <summary>One cube in the game's model space (Y down, front -Z): min corner, size, skin offset, inflation.</summary>
    public struct ModelBox
    {
        public float X, Y, Z, W, H, D, Grow;
        public int U, V;
        public ModelBox(int u, int v, float x, float y, float z, float w, float h, float d, float grow = 0)
        { U = u; V = v; X = x; Y = y; Z = z; W = w; H = h; D = d; Grow = grow; }
    }

    /// <summary>A body part: pivot (game model space) plus the base box and the second-layer (overlay) box.</summary>
    public struct PartDef
    {
        public PlayerPart Part;
        public float PivotX, PivotY, PivotZ;
        public ModelBox Base, Overlay;
    }

    /// <summary>Vertex buffers: 3 floats per position/normal, 2 per uv (Unity convention, v = 0 at the image bottom).</summary>
    public sealed class FigureMesh
    {
        public readonly List<float> Pos = new List<float>(), Normal = new List<float>(), Uv = new List<float>();
        public readonly List<int> Tris = new List<int>();
        public int VertexCount => Pos.Count / 3;
        public void Clear() { Pos.Clear(); Normal.Clear(); Uv.Clear(); Tris.Clear(); }
    }

    public static class PlayerModel
    {
        public const int PartCount = 6;
        public const float SkinSize = 64f;
        /// <summary>Model pixels from the feet to the neck pivot (the game's model Y = 0 is 24 px above the feet).</summary>
        public const float NeckHeight = 24f;
        /// <summary>Vanilla crouch: the whole model drops 2 px.</summary>
        public const float CrouchDrop = 2f;

        /// <summary>Parts as in the 26.x client (PlayerModel.createMesh): slim arms are 3 px wide; both arm pivots are y = 2.</summary>
        public static PartDef[] Parts(bool slim)
        {
            float aw = slim ? 3 : 4;
            return new[]
            {
                new PartDef { Part = PlayerPart.Head, Base = new ModelBox(0, 0, -4, -8, -4, 8, 8, 8), Overlay = new ModelBox(32, 0, -4, -8, -4, 8, 8, 8, 0.5f) },
                new PartDef { Part = PlayerPart.Body, Base = new ModelBox(16, 16, -4, 0, -2, 8, 12, 4), Overlay = new ModelBox(16, 32, -4, 0, -2, 8, 12, 4, 0.25f) },
                new PartDef { Part = PlayerPart.RightArm, PivotX = -5, PivotY = 2,
                    Base = new ModelBox(40, 16, slim ? -2 : -3, -2, -2, aw, 12, 4), Overlay = new ModelBox(40, 32, slim ? -2 : -3, -2, -2, aw, 12, 4, 0.25f) },
                new PartDef { Part = PlayerPart.LeftArm, PivotX = 5, PivotY = 2,
                    Base = new ModelBox(32, 48, -1, -2, -2, aw, 12, 4), Overlay = new ModelBox(48, 48, -1, -2, -2, aw, 12, 4, 0.25f) },
                new PartDef { Part = PlayerPart.RightLeg, PivotX = -1.9f, PivotY = 12,
                    Base = new ModelBox(0, 16, -2, 0, -2, 4, 12, 4), Overlay = new ModelBox(0, 32, -2, 0, -2, 4, 12, 4, 0.25f) },
                new PartDef { Part = PlayerPart.LeftLeg, PivotX = 1.9f, PivotY = 12,
                    Base = new ModelBox(16, 48, -2, 0, -2, 4, 12, 4), Overlay = new ModelBox(0, 48, -2, 0, -2, 4, 12, 4, 0.25f) },
            };
        }

        /// <summary>
        /// Game model space -> figure space: (x, y, z) -> (-x, 24 - y, -z) for points, (-x, -y, -z) for offsets.
        /// It's the game's render transform (flip X/Y, turn 180 degrees for yaw 0) followed by the tabletop Z mirror.
        /// The map is a point reflection, so part rotations keep their numeric values (R commutes with -I).
        /// </summary>
        public static void Pivot(PartDef p, out float x, out float y, out float z) { x = -p.PivotX; y = NeckHeight - p.PivotY; z = -p.PivotZ; }

        /// <summary>Base and overlay boxes of a part, relative to its pivot, in figure space.</summary>
        public static void BuildPart(PartDef p, FigureMesh m)
        {
            AppendBox(p.Base, m);
            AppendBox(p.Overlay, m);
        }

        // Face order and corner -> UV assignment follow ModelPart.Cube / ModelPart.Polygon exactly.
        // Corners: 0=(x0,y0,z0) 1=(x1,y0,z0) 2=(x1,y1,z0) 3=(x0,y1,z0) 4=(x0,y0,z1) 5=(x1,y0,z1) 6=(x1,y1,z1) 7=(x0,y1,z1).
        static readonly int[][] FaceCorners =
        {
            new[] { 5, 4, 0, 1 }, // DOWN (game -Y) = top of the part
            new[] { 2, 3, 7, 6 }, // UP (game +Y) = bottom
            new[] { 0, 4, 7, 3 }, // WEST (game -X) = the character's right side
            new[] { 1, 0, 3, 2 }, // NORTH (game -Z) = front
            new[] { 5, 1, 2, 6 }, // EAST (game +X) = left side
            new[] { 4, 5, 6, 7 }, // SOUTH (game +Z) = back
        };
        // Outward normal of each face in figure space (the game's direction, point-reflected).
        static readonly float[][] FaceNormals = { new[] { 0f, 1, 0 }, new[] { 0f, -1, 0 }, new[] { 1f, 0, 0 }, new[] { 0f, 0, 1 }, new[] { -1f, 0, 0 }, new[] { 0f, 0, -1 } };

        public static void AppendBox(ModelBox b, FigureMesh m)
        {
            float x0 = b.X - b.Grow, y0 = b.Y - b.Grow, z0 = b.Z - b.Grow;
            float x1 = b.X + b.W + b.Grow, y1 = b.Y + b.H + b.Grow, z1 = b.Z + b.D + b.Grow;
            float[,] c =
            {
                { x0, y0, z0 }, { x1, y0, z0 }, { x1, y1, z0 }, { x0, y1, z0 },
                { x0, y0, z1 }, { x1, y0, z1 }, { x1, y1, z1 }, { x0, y1, z1 },
            };
            float u = b.U, v = b.V, w = b.W, h = b.H, d = b.D;
            // (u1, v1, u2, v2) per face, in skin pixels (v down), as the game passes them to Polygon.
            float[][] uv =
            {
                new[] { u + d, v, u + d + w, v + d },
                new[] { u + d + w, v + d, u + d + w + w, v },
                new[] { u, v + d, u + d, v + d + h },
                new[] { u + d, v + d, u + d + w, v + d + h },
                new[] { u + d + w, v + d, u + d + w + d, v + d + h },
                new[] { u + d + w + d, v + d, u + d + w + d + w, v + d + h },
            };
            for (int f = 0; f < 6; f++)
            {
                int start = m.VertexCount;
                var q = uv[f];
                // Polygon: corner 0 -> (u2, v1), 1 -> (u1, v1), 2 -> (u1, v2), 3 -> (u2, v2).
                float[] cu = { q[2], q[0], q[0], q[2] }, cv = { q[1], q[1], q[3], q[3] };
                for (int k = 0; k < 4; k++)
                {
                    int ci = FaceCorners[f][k];
                    m.Pos.Add(-c[ci, 0]); m.Pos.Add(-c[ci, 1]); m.Pos.Add(-c[ci, 2]);
                    m.Normal.Add(FaceNormals[f][0]); m.Normal.Add(FaceNormals[f][1]); m.Normal.Add(FaceNormals[f][2]);
                    m.Uv.Add(cu[k] / SkinSize); m.Uv.Add(1f - cv[k] / SkinSize);
                }
                // The game's corner order is counter-clockwise seen from outside; the point reflection keeps how it
                // looks on screen, and Unity wants clockwise front faces, so reverse the winding.
                m.Tris.Add(start); m.Tris.Add(start + 2); m.Tris.Add(start + 1);
                m.Tris.Add(start); m.Tris.Add(start + 3); m.Tris.Add(start + 2);
            }
        }
    }
}
