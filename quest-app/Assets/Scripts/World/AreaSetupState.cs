// Engine-free state for the headset area-setup mode (AreaSetupMode drives it from controllers):
// where the centre is (a named player, the current origin, or typed/nudged coordinates), the box size in blocks,
// the server limits, the mesh budget check, and the region_request that confirming sends.
// Size is the visible box. The request adds a view margin on each side (NavState: smooth panning), which shrinks
// when the box is close to the server's caps.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public enum CenterMode { CurrentOrigin, Player, Coordinates }

    public sealed class AreaSetupState
    {
        public CenterMode Mode = CenterMode.CurrentOrigin;
        public string World = "world";
        public Vector3 Center;                 // world block coordinates (x, y, z)
        public Vector3Int Size = new Vector3Int(32, 96, 32);
        public int YOrigin = 32;               // bottom of the box in world Y
        public RegionLimits Limits = RegionLimits.Default;
        public long QuadBudget = AreaMath.DefaultQuadBudget;
        public bool OverrideBudget;
        public readonly List<RegionPlayer> Players = new List<RegionPlayer>();
        public int PlayerIndex;
        public const int Step = 8;             // horizontal resize granularity (blocks)
        public Vector3Int MarginPref = new Vector3Int(16, 16, 16);   // view margin per side (NavState.MarginPref)
        public int MinY = int.MinValue, MaxY = int.MaxValue;         // build height, when the server sends it

        Vector3Int _curOrigin, _curDims;

        /// <summary>Start from the region currently shown (region_info or the last snapshot).</summary>
        public void Begin(string world, Vector3Int origin, Vector3Int dims)
        {
            World = world ?? World; _curOrigin = origin; _curDims = dims;
            Size = dims; YOrigin = origin.y; Mode = CenterMode.CurrentOrigin; OverrideBudget = false;
            ApplyMode();
        }

        public void SetInfo(RegionInfo info)
        {
            Limits = info.Limits;
            Players.Clear(); Players.AddRange(info.Players);
            if (PlayerIndex >= Players.Count) PlayerIndex = 0;
            ApplyMode();
        }

        public void CycleMode() { Mode = (CenterMode)(((int)Mode + 1) % 3); ApplyMode(); }

        public void NextPlayer(int dir)
        {
            if (Players.Count == 0) return;
            PlayerIndex = ((PlayerIndex + dir) % Players.Count + Players.Count) % Players.Count;
            Mode = CenterMode.Player; ApplyMode();
        }

        void ApplyMode()
        {
            if (Mode == CenterMode.CurrentOrigin)
                Center = new Vector3(_curOrigin.x + _curDims.x / 2f, _curOrigin.y + _curDims.y / 2f, _curOrigin.z + _curDims.z / 2f);
            else if (Mode == CenterMode.Player && Players.Count > 0)
            {
                var p = Players[PlayerIndex];
                Center = new Vector3((float)Math.Floor(p.Pos.x), (float)Math.Floor(p.Pos.y), (float)Math.Floor(p.Pos.z));
                if (!string.IsNullOrEmpty(p.World)) World = p.World;
                // Keep the player's feet ~1/3 up the box so terrain below them is included.
                YOrigin = (int)Math.Floor(p.Pos.y) - Size.y / 3;
            }
        }

        /// <summary>Move the centre by whole blocks (coordinates mode; switches to it).</summary>
        public void Nudge(int dx, int dy, int dz)
        {
            Mode = CenterMode.Coordinates;
            Center = new Vector3(Center.x + dx, Center.y + dy, Center.z + dz);
            YOrigin += dy;
        }

        /// <summary>Grow/shrink one axis (0=x width, 1=y height, 2=z depth) by steps, clamped to the server limits.</summary>
        public void Resize(int axis, int steps)
        {
            var s = Size;
            int step = axis == 1 ? 16 : Step;
            s[axis] = Math.Max(axis == 1 ? 16 : Step, (s[axis] / step + steps) * step);
            var (x, y, z) = AreaMath.ClampToCaps(s.x, s.y, s.z, Limits.MaxX, Limits.MaxY, Limits.MaxZ, Limits.MaxBlocks);
            Size = new Vector3Int(x, y, z);
        }

        /// <summary>Margin actually requested: MarginPref, shrunk to fit the caps, the block limit and the build height.</summary>
        public Vector3Int RequestMargin
        {
            get
            {
                var m = new Vector3Int(Math.Max(0, Math.Min(MarginPref.x, (Limits.MaxX - Size.x) / 2)), Math.Max(0, Math.Min(MarginPref.y, (Limits.MaxY - Size.y) / 2)),
                                       Math.Max(0, Math.Min(MarginPref.z, (Limits.MaxZ - Size.z) / 2)));
                if (MinY != int.MinValue) m.y = Math.Max(0, Math.Min(m.y, YOrigin - MinY));
                if (MaxY != int.MaxValue) m.y = Math.Max(0, Math.Min(m.y, MaxY - (YOrigin + Size.y)));
                while ((long)(Size.x + 2 * m.x) * (Size.y + 2 * m.y) * (Size.z + 2 * m.z) > Limits.MaxBlocks && (m.x > 0 || m.y > 0 || m.z > 0))
                {
                    if (m.y > 0) m.y = Math.Max(0, m.y - 8); else { m.x = Math.Max(0, m.x - 8); m.z = Math.Max(0, m.z - 8); }
                }
                return m;
            }
        }
        public Vector3Int RequestDims { get { var m = RequestMargin; return new Vector3Int(Size.x + 2 * m.x, Size.y + 2 * m.y, Size.z + 2 * m.z); } }

        /// <summary>Everything in the request gets meshed (the margin too: it slides into view), so estimate that.</summary>
        public MeshEstimate Estimate { get { var d = RequestDims; return AreaMath.Estimate(d.x, d.y, d.z); } }
        public long SectionBudget = AreaMath.DefaultSectionBudget;
        public long EstimatedMeshes { get { var d = RequestDims; return AreaMath.EstimateMeshes(d.x, d.y, d.z); } }
        public bool OverBudget => AreaMath.OverBudget(Estimate, QuadBudget, OverrideBudget) || (!OverrideBudget && EstimatedMeshes > SectionBudget);
        public long Blocks => (long)Size.x * Size.y * Size.z;

        /// <summary>Why confirming is blocked, or null when the request can be sent.</summary>
        public string Blocker()
        {
            if (Size.x > Limits.MaxX || Size.y > Limits.MaxY || Size.z > Limits.MaxZ || Blocks > Limits.MaxBlocks)
                return $"over server limit {Limits.MaxX}x{Limits.MaxY}x{Limits.MaxZ} / {Limits.MaxBlocks} blocks";
            if (OverBudget && EstimatedMeshes > SectionBudget)
                return $"~{EstimatedMeshes} section meshes is over the headset budget (~{SectionBudget} draw calls). Hold B to override";
            if (OverBudget) return $"{Estimate} is over the headset budget (~{QuadBudget / 1000}k faces). Hold B to override";
            return null;
        }

        public Dictionary<string, object> BuildRequest()
        {
            var m = RequestMargin; var d = RequestDims;
            var r = Protocol.RegionRequest(World, new Vector3(Center.x, YOrigin - m.y + d.y / 2f, Center.z), d, YOrigin - m.y);
            r["view_margin"] = new List<object> { m.x, m.y, m.z };   // PROTOCOL.md 4.17
            return r;
        }

        public string Describe() =>
            $"AREA {Size.x}x{Size.y}x{Size.z} ({Blocks / 1000}k blocks) centre {Mode}" +
            (Mode == CenterMode.Player && Players.Count > 0 ? $" {Players[PlayerIndex].Name}" : "") +
            $" ({Center.x:F0},{YOrigin}..{YOrigin + Size.y},{Center.z:F0})\n{Estimate}{(OverrideBudget ? " [override]" : "")}" +
            (RequestMargin != default ? $"  +{RequestMargin.x}/{RequestMargin.y} margin" : "");
    }
}
