// Sign text the way vanilla 26.3 draws it (AbstractSignRenderer): the pack's bitmap font (font/default.json and its
// includes: "space" advances and "bitmap" glyph sheets; glyph widths measured from the sheets like BitmapProvider),
// lines centred, line height 10 (hanging signs 9) from -2 lines up, wrapped to 90 (60) px, and the colours: plain
// text in its dye's text colour times 0.4, lit by the block; glowing text in the full colour at full brightness with
// an 8-direction outline in the darker colour (cream for black glowing text). Engine-free: Tests~ lays out text with
// the real client jar; BlockEntityRenderer turns the quads into one mesh.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class BitmapFont
    {
        public struct Glyph
        {
            public float U0, V0, U1, V1;        // atlas UVs (Unity orientation, v up)
            public float Right, Top, Bottom;    // quad in font pixels: x 0..Right, y Top..Bottom (y down)
            public float Advance;
        }

        readonly Dictionary<int, Glyph> _glyphs = new Dictionary<int, Glyph>();
        readonly Dictionary<int, float> _spaces = new Dictionary<int, float>();
        /// <summary>All glyph sheets stacked vertically (row 0 = bottom, like Texture2D.SetPixels32).</summary>
        public Color32[] Atlas;
        public int AtlasW, AtlasH;
        public int GlyphCount => _glyphs.Count;
        public const float MissingAdvance = 6;

        public bool TryGlyph(int cp, out Glyph g) => _glyphs.TryGetValue(cp, out g);

        public float Advance(int cp) => _spaces.TryGetValue(cp, out float a) ? a : _glyphs.TryGetValue(cp, out var g) ? g.Advance : MissingAdvance;

        public float Width(string s)
        {
            float w = 0;
            for (int i = 0; i < s.Length; i++) { int cp = Utf32Ext.ConvertToUtf32OrSelf(s, ref i); w += Advance(cp); }
            return w;
        }

        sealed class Sheet { public Color32[] Px; public int W, H, Y; }

        /// <summary>Reads font/default.json (and its references) from a pack. readPng returns bottom-up pixels or null.
        /// Null when the pack has no usable bitmap font.</summary>
        public static BitmapFont Load(Func<string, Dictionary<string, object>> readJson, Func<string, (Color32[] px, int w, int h)?> readPng)
        {
            var f = new BitmapFont();
            var sheets = new List<Sheet>();
            var pending = new List<(int cp, Sheet sheet, int cx, int cy, int cw, int ch, float scale, int ascent)>();
            f.AddFont("minecraft:default", readJson, readPng, sheets, pending, 0);
            if (sheets.Count == 0) return null;
            // Stack the sheets: atlas width = widest sheet, each sheet at its own y (bottom-up order kept per sheet).
            int w = 0, h = 0;
            foreach (var s in sheets) { s.Y = h; h += s.H; w = Math.Max(w, s.W); }
            f.AtlasW = w; f.AtlasH = h;
            f.Atlas = new Color32[w * h];
            foreach (var s in sheets)
                for (int y = 0; y < s.H; y++) Array.Copy(s.Px, y * s.W, f.Atlas, (s.Y + y) * w, s.W);
            foreach (var p in pending)
            {
                if (f._glyphs.ContainsKey(p.cp) || f._spaces.ContainsKey(p.cp)) continue;   // the first provider wins
                int actual = ActualWidth(p.sheet, p.cx, p.cy, p.cw, p.ch);
                // Sheet rows are bottom-up: the cell's top row in the image is at sheet row H - 1 - cy.
                float u0 = (float)p.cx / w, u1 = (float)(p.cx + p.cw) / w;
                float vTop = (float)(p.sheet.Y + p.sheet.H - p.cy) / h, vBot = (float)(p.sheet.Y + p.sheet.H - p.cy - p.ch) / h;
                float top = 7 - p.ascent;
                f._glyphs[p.cp] = new Glyph
                {
                    U0 = u0, U1 = u1, V0 = vBot, V1 = vTop,
                    Right = p.cw * p.scale, Top = top, Bottom = top + p.ch * p.scale,
                    Advance = (int)(0.5 + actual * p.scale) + 1,
                };
            }
            return f;
        }

        void AddFont(string id, Func<string, Dictionary<string, object>> readJson, Func<string, (Color32[] px, int w, int h)?> readPng,
                     List<Sheet> sheets, List<(int, Sheet, int, int, int, int, float, int)> pending, int depth)
        {
            if (depth > 4) return;
            Split(id, out string ns, out string path);
            var def = readJson($"assets/{ns}/font/{path}.json");
            if (!(def?.A("providers") is List<object> providers)) return;
            foreach (var o in providers)
            {
                if (!(o is Dictionary<string, object> pr)) continue;
                // Options-dependent providers: the defaults are uniform font off and Japanese glyph variants off.
                if (pr.O("filter") is Dictionary<string, object> filter && filter.TryGetValue("uniform", out var u) && u is bool ub && ub) continue;
                string type = (pr.S("type") ?? "").Replace("minecraft:", "");
                if (type == "reference") AddFont(pr.S("id") ?? "", readJson, readPng, sheets, pending, depth + 1);
                else if (type == "space" && pr.O("advances") is Dictionary<string, object> adv)
                {
                    foreach (var kv in adv)
                        if (kv.Key.Length > 0 && kv.Value is double a) { int k0 = 0; int cp = Utf32Ext.ConvertToUtf32OrSelf(kv.Key, ref k0); if (!_spaces.ContainsKey(cp) && !_glyphs.ContainsKey(cp)) _spaces[cp] = (float)a; }
                }
                else if (type == "bitmap" && pr.A("chars") is List<object> rows && rows.Count > 0)
                {
                    Split(pr.S("file") ?? "", out string fns, out string fpath);
                    var img = readPng($"assets/{fns}/textures/{fpath}");
                    if (img == null) continue;
                    var sheet = new Sheet { Px = img.Value.px, W = img.Value.w, H = img.Value.h };
                    sheets.Add(sheet);
                    int cols = 0;
                    var cps = new List<List<int>>();
                    foreach (var r in rows)
                    {
                        var line = new List<int>();
                        string s = r as string ?? "";
                        for (int i = 0; i < s.Length; i++) line.Add(Utf32Ext.ConvertToUtf32OrSelf(s, ref i));
                        cps.Add(line);
                        cols = Math.Max(cols, line.Count);
                    }
                    if (cols == 0) continue;
                    int cw = sheet.W / cols, ch = sheet.H / cps.Count;
                    int height = pr.I("height", 8), ascent = pr.I("ascent", 7);
                    float scale = (float)height / ch;
                    for (int ry = 0; ry < cps.Count; ry++)
                        for (int cx = 0; cx < cps[ry].Count; cx++)
                        {
                            int cp = cps[ry][cx];
                            if (cp == 0) continue;
                            pending.Add((cp, sheet, cx * cw, ry * ch, cw, ch, scale, ascent));
                        }
                }
                // ttf and unihex providers (unifont) are not supported: characters only they cover are left out.
            }
        }

        /// <summary>BitmapProvider.getActualGlyphWidth: 1 + the rightmost column with any non-transparent pixel.</summary>
        static int ActualWidth(Sheet s, int x0, int y0, int w, int h)
        {
            for (int x = w - 1; x >= 0; x--)
                for (int y = 0; y < h; y++)
                {
                    int row = s.H - 1 - (y0 + y);   // image rows top-down -> bottom-up storage
                    if (s.Px[row * s.W + x0 + x].a != 0) return x + 1;
                }
            return 0;
        }

        static void Split(string id, out string ns, out string path)
        {
            int c = id.IndexOf(':');
            ns = c >= 0 ? id.Substring(0, c) : "minecraft";
            path = c >= 0 ? id.Substring(c + 1) : id;
        }
    }

    static class Utf32Ext
    {
        /// <summary>The code point at i (advancing i over a surrogate pair), or the char itself when unpaired.</summary>
        public static int ConvertToUtf32OrSelf(string s, ref int i)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { int cp = char.ConvertToUtf32(c, s[i + 1]); i++; return cp; }
            return c;
        }
    }

    /// <summary>One glyph quad in text pixels (x right, y down, z towards the reader) with its UVs and colour (sRGB).</summary>
    public struct TextQuad
    {
        public float X0, Y0, X1, Y1, Z;
        public float U0, V0, U1, V1;
        public Color32 Color;
        public bool FullBright;   // glowing text ignores the block's light
    }

    public static class SignTextLayout
    {
        /// <summary>The cream outline vanilla gives black glowing text (AbstractSignRenderer.BLACK_TEXT_OUTLINE_COLOR).</summary>
        public static readonly Color32 BlackGlowOutline = new Color32(0xF0, 0xEB, 0xCC, 255);

        /// <summary>ARGB.scaleRGB(c, 0.4): the plain (not glowing) text colour and the glow outline.</summary>
        public static Color32 Dark(Color32 c) => new Color32((byte)(int)(c.r * 0.4f), (byte)(int)(c.g * 0.4f), (byte)(int)(c.b * 0.4f), 255);

        /// <summary>The first line Font.split(text, maxWidth) gives: wrapped at the last space that fits, else cut.</summary>
        public static string Fit(BitmapFont font, string s, float maxWidth)
        {
            if (string.IsNullOrEmpty(s) || font.Width(s) <= maxWidth) return s ?? "";
            float w = 0;
            int lastSpace = -1;
            for (int i = 0; i < s.Length; i++)
            {
                int start = i;
                int cp = Utf32Ext.ConvertToUtf32OrSelf(s, ref i);
                if (cp == ' ') lastSpace = start;
                w += font.Advance(cp);
                if (w > maxWidth) return lastSpace > 0 ? s.Substring(0, lastSpace) : s.Substring(0, start);
            }
            return s;
        }

        /// <summary>Lays out one sign side: up to 4 lines centred, glowing text with its outline. textColor is the dye's
        /// text colour (DyeColor.getTextColor).</summary>
        public static void Layout(BitmapFont font, SignSideText side, Color32 textColor, int lineHeight, int maxWidth, List<TextQuad> into)
        {
            if (font == null || side == null || side.Empty) return;
            bool black = side.Color == "black";
            Color32 dark = black && side.Glowing ? BlackGlowOutline : Dark(textColor);
            Color32 main = side.Glowing ? textColor : dark;
            bool outline = side.Glowing;   // vanilla draws it for coloured glowing text, and for black glowing text up close
            for (int line = 0; line < 4; line++)
            {
                string s = Fit(font, side.Lines[line], maxWidth);
                if (string.IsNullOrEmpty(s)) continue;
                float x = -(int)font.Width(s) / 2;   // -Font.width / 2 in integers
                float y = line * lineHeight - 4 * lineHeight / 2;
                for (int i = 0; i < s.Length; i++)
                {
                    int cp = Utf32Ext.ConvertToUtf32OrSelf(s, ref i);
                    if (cp != ' ' && font.TryGlyph(cp, out var g))
                    {
                        if (outline)
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                    if (dx != 0 || dy != 0) Add(into, g, x + dx, y + dy, -0.25f, dark, true);
                        Add(into, g, x, y, 0, main, side.Glowing);
                    }
                    x += font.Advance(cp);
                }
            }
        }

        static void Add(List<TextQuad> into, BitmapFont.Glyph g, float x, float y, float z, Color32 c, bool bright) =>
            into.Add(new TextQuad { X0 = x, X1 = x + g.Right, Y0 = y + g.Top, Y1 = y + g.Bottom, Z = z, U0 = g.U0, U1 = g.U1, V0 = g.V1, V1 = g.V0, Color = c, FullBright = bright });
    }
}
