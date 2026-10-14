// Skin texture rules from the game client, engine-free: legacy 64x32 -> 64x64 conversion and alpha fix-ups
// (SkinTextureDownloader.processLegacySkin), the default skin for a UUID (DefaultPlayerSkin.get), the allowed skin
// URLs, and a plain-colour fallback skin. Images are RGBA32, rows top-down (row 0 = top of the PNG); the Unity side
// flips rows when it reads or writes a Texture2D.
using System;

namespace TabletopCraft.World
{
    public static class SkinImage
    {
        public const int Size = 64;

        /// <summary>
        /// Returns a 64x64 image ready to render: a 64x32 legacy skin gets its left arm/leg copied (mirrored) from the
        /// right ones, and the base layer is made opaque like the game does. Null for any other size.
        /// </summary>
        public static byte[] Process(byte[] rgba, int w, int h)
        {
            if (rgba == null || w != Size || (h != 32 && h != 64) || rgba.Length < w * h * 4) return null;
            bool legacy = h == 32;
            var img = new byte[Size * Size * 4];
            Buffer.BlockCopy(rgba, 0, img, 0, w * h * 4);
            if (legacy)
            {
                // fillRect(0, 32, 64, 32, 0) is implied: the new lower half is already transparent black.
                CopyRect(img, 4, 16, 16, 32, 4, 4, true);
                CopyRect(img, 8, 16, 16, 32, 4, 4, true);
                CopyRect(img, 0, 20, 24, 32, 4, 12, true);
                CopyRect(img, 4, 20, 16, 32, 4, 12, true);
                CopyRect(img, 8, 20, 8, 32, 4, 12, true);
                CopyRect(img, 12, 20, 16, 32, 4, 12, true);
                CopyRect(img, 44, 16, -8, 32, 4, 4, true);
                CopyRect(img, 48, 16, -8, 32, 4, 4, true);
                CopyRect(img, 40, 20, 0, 32, 4, 12, true);
                CopyRect(img, 44, 20, -8, 32, 4, 12, true);
                CopyRect(img, 48, 20, -16, 32, 4, 12, true);
                CopyRect(img, 52, 20, -8, 32, 4, 12, true);
            }
            SetNoAlpha(img, 0, 0, 32, 16);
            if (legacy) NotchTransparencyHack(img, 32, 0, 64, 32);
            SetNoAlpha(img, 0, 16, 64, 32);
            SetNoAlpha(img, 16, 48, 48, 64);
            return img;
        }

        /// <summary>NativeImage.copyRect: copy w x h at (x, y) to (x + dx, y + dy), optionally mirrored horizontally.</summary>
        static void CopyRect(byte[] img, int x, int y, int dx, int dy, int w, int h, bool mirrorX)
        {
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    int ti = mirrorX ? w - 1 - i : i;
                    int src = ((y + j) * Size + x + i) * 4, dst = ((y + dy + j) * Size + x + dx + ti) * 4;
                    Buffer.BlockCopy(img, src, img, dst, 4);
                }
        }

        static void SetNoAlpha(byte[] img, int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) img[(y * Size + x) * 4 + 3] = 255;
        }

        /// <summary>Old skins painted the hat area solid: if no pixel there is transparent, clear it all.</summary>
        static void NotchTransparencyHack(byte[] img, int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) if (img[(y * Size + x) * 4 + 3] < 128) return;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) img[(y * Size + x) * 4 + 3] = 0;
        }

        // DefaultPlayerSkin.DEFAULT_SKINS, in the game's order (slim first, then wide).
        static readonly string[] DefaultNames = { "alex", "ari", "efe", "kai", "makena", "noor", "steve", "sunny", "zuri" };

        /// <summary>
        /// The default skin the game shows for a player without textures: DEFAULT_SKINS[floorMod(uuid.hashCode(), 18)].
        /// Returns the pack path (e.g. assets/minecraft/textures/entity/player/wide/steve.png) and whether it is slim.
        /// Falls back to wide Steve for an unparsable UUID.
        /// </summary>
        public static string DefaultSkinPath(string uuid, out bool slim)
        {
            int idx = 9 + 6; // wide steve
            if (TryJavaUuidHash(uuid, out int hash)) idx = (int)(((long)hash % 18 + 18) % 18);
            slim = idx < 9;
            return $"assets/minecraft/textures/entity/player/{(slim ? "slim" : "wide")}/{DefaultNames[idx % 9]}.png";
        }

        public const string SteveWidePath = "assets/minecraft/textures/entity/player/wide/steve.png";

        /// <summary>java.util.UUID.hashCode(): hilo = msb ^ lsb; (int)(hilo >> 32) ^ (int)hilo.</summary>
        public static bool TryJavaUuidHash(string uuid, out int hash)
        {
            hash = 0;
            if (string.IsNullOrEmpty(uuid)) return false;
            string hex = uuid.Replace("-", "");
            if (hex.Length != 32) return false;
            try
            {
                ulong msb = Convert.ToUInt64(hex.Substring(0, 16), 16), lsb = Convert.ToUInt64(hex.Substring(16, 16), 16);
                ulong hilo = msb ^ lsb;
                hash = (int)(uint)(hilo >> 32) ^ (int)(uint)hilo;
                return true;
            }
            catch (FormatException) { return false; }
        }

        /// <summary>
        /// Skin URLs the headset will fetch: textures.minecraft.net only (where profile textures live), upgraded to
        /// https. Anything else returns null, so a server can't make the headset fetch arbitrary URLs.
        /// </summary>
        public static string NormalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return null;
            if (u.Scheme != "http" && u.Scheme != "https") return null;
            if (!string.Equals(u.Host, "textures.minecraft.net", StringComparison.OrdinalIgnoreCase)) return null;
            if (!u.AbsolutePath.StartsWith("/texture/") || u.AbsolutePath.Length <= "/texture/".Length) return null;
            return "https://textures.minecraft.net" + u.AbsolutePath;
        }

        static readonly byte[][] Shirts = { new byte[] { 0, 170, 170 }, new byte[] { 200, 60, 60 }, new byte[] { 70, 160, 70 }, new byte[] { 220, 170, 40 }, new byte[] { 140, 80, 180 } };
        public static int PlainVariants => Shirts.Length;

        /// <summary>Which plain-colour variant (shirt colour) a UUID gets.</summary>
        public static int PlainVariant(string uuid)
        {
            int hash = TryJavaUuidHash(uuid, out int h) ? h : 0;
            return (int)((uint)hash % (uint)Shirts.Length);
        }

        /// <summary>
        /// A skin with plain colours only (no Mojang art): skin-tone head and arms, a shirt, trousers and shoes, so the
        /// figure still reads as a person when there's no skin URL and no pack.
        /// </summary>
        public static byte[] PlainColours(int variant)
        {
            var img = new byte[Size * Size * 4];
            byte[] skin = { 198, 140, 105 }, hair = { 70, 45, 30 }, pants = { 50, 60, 140 }, shoes = { 60, 60, 60 };
            byte[] shirt = Shirts[((variant % Shirts.Length) + Shirts.Length) % Shirts.Length];
            void Fill(int x0, int y0, int x1, int y1, byte[] c) { for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = (y * Size + x) * 4; img[i] = c[0]; img[i + 1] = c[1]; img[i + 2] = c[2]; img[i + 3] = 255; } }
            Fill(0, 0, 32, 16, skin);                       // head
            Fill(8, 0, 16, 8, hair); Fill(0, 8, 32, 10, hair); // top of the head and a fringe all round
            Fill(10, 12, 11, 13, shoes); Fill(13, 12, 14, 13, shoes); // eyes on the front face (8..16, 8..16)
            Fill(16, 16, 40, 32, shirt);                    // body
            Fill(40, 16, 56, 32, skin); Fill(32, 48, 48, 64, skin);   // arms
            Fill(40, 16, 56, 23, shirt); Fill(32, 48, 48, 55, shirt); // short sleeves
            Fill(0, 16, 16, 32, pants); Fill(16, 48, 32, 64, pants);  // legs
            Fill(0, 29, 16, 32, shoes); Fill(16, 61, 32, 64, shoes);  // shoes
            return img;
        }
    }
}
