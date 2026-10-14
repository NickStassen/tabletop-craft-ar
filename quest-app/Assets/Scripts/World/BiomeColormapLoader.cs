// Loads the pack's biome colour maps (assets/minecraft/textures/colormap/{grass,foliage,dry_foliage}.png, 256x256) into
// BiomeColors as 0xRRGGBB arrays in PNG row order (top row first), the layout vanilla's ColorMapColorUtil indexes.
// Main thread only (Texture2D.LoadImage). A pack without colour maps keeps the fixed tints.
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace TabletopCraft.World
{
    public static class BiomeColormapLoader
    {
        public static void Load(ZipArchive zip)
        {
            BiomeColors.SetColormaps(Read(zip, "grass"), Read(zip, "foliage"), Read(zip, "dry_foliage"));
            Debug.Log($"[Tabletop] biome colour maps: grass {BiomeColors.GrassMap != null} foliage {BiomeColors.FoliageMap != null} dry_foliage {BiomeColors.DryFoliageMap != null}");
        }

        static int[] Read(ZipArchive zip, string name)
        {
            var e = zip?.GetEntry($"assets/minecraft/textures/colormap/{name}.png");
            if (e == null) return null;
            byte[] bytes;
            using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); bytes = ms.ToArray(); }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(bytes) || tex.width != 256 || tex.height != 256) return null;
                var px = tex.GetPixels32(); // bottom row first in Unity
                var map = new int[256 * 256];
                for (int y = 0; y < 256; y++)
                    for (int x = 0; x < 256; x++)
                    {
                        var c = px[(255 - y) * 256 + x];
                        map[y * 256 + x] = (c.r << 16) | (c.g << 8) | c.b;
                    }
                return map;
            }
            finally { Object.Destroy(tex); }
        }
    }
}
