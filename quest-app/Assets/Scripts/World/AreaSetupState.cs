// Engine-free state for the headset area-setup mode (AreaSetupMode drives it from controllers):
// where the centre is (a named player, the current origin, or typed/nudged coordinates), the box size in blocks,
// the server limits, the mesh budget check, and the region_request that confirming sends.
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

        public MeshEstimate Estimate => AreaMath.Estimate(Size.x, Size.y, Size.z);
        public bool OverBudget => AreaMath.OverBudget(Estimate, QuadBudget, OverrideBudget);
        public long Blocks => (long)Size.x * Size.y * Size.z;

        /// <summary>Why confirming is blocked, or null when the request can be sent.</summary>
        public string Blocker()
        {
            if (Size.x > Limits.MaxX || Size.y > Limits.MaxY || Size.z > Limits.MaxZ || Blocks > Limits.MaxBlocks)
                return $"over server limit {Limits.MaxX}x{Limits.MaxY}x{Limits.MaxZ} / {Limits.MaxBlocks} blocks";
            if (OverBudget) return $"{Estimate} is over the headset budget (~{QuadBudget / 1000}k faces). Hold B to override";
            return null;
        }

        public Dictionary<string, object> BuildRequest() =>
            Protocol.RegionRequest(World, new Vector3(Center.x, YOrigin + Size.y / 2f, Center.z), Size, YOrigin);

        public string Describe() =>
            $"AREA {Size.x}x{Size.y}x{Size.z} ({Blocks / 1000}k blocks) centre {Mode}" +
            (Mode == CenterMode.Player && Players.Count > 0 ? $" {Players[PlayerIndex].Name}" : "") +
            $" ({Center.x:F0},{YOrigin}..{YOrigin + Size.y},{Center.z:F0})\n{Estimate}{(OverrideBudget ? " [override]" : "")}";
    }
}
