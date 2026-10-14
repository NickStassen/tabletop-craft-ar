// Minecraft texture animation: the "animation" section of <texture>.png.mcmeta and its frame schedule. Engine-free
// (tested in Tests~ against the real client jar); ResourcePackAtlas.Animation.cs does the GPU side.
// Rules follow the vanilla client: frames are squares of min(width, height) unless "width"/"height" are given, laid
// out left to right then top to bottom; "frametime" (ticks, default 1) is the default per-frame time; "frames" is an
// optional list of indices or {"index", "time"}; "interpolate" blends RGB toward the next frame (alpha of the
// current one). The game advances animations at 20 ticks per second.
using System.Collections.Generic;
using TabletopCraft.Net;

namespace TabletopCraft.World
{
    public sealed class TextureAnimation
    {
        public const int TicksPerSecond = 20;

        public struct Frame { public int Index, Time; }

        /// <summary>Playback order. Index is an image frame, Time is in ticks (at least 1).</summary>
        public readonly Frame[] Frames;
        public readonly bool Interpolate;
        public readonly int FrameWidth, FrameHeight, Columns, ImageFrames, CycleTicks;

        TextureAnimation(Frame[] frames, bool interpolate, int fw, int fh, int columns, int imageFrames)
        {
            Frames = frames; Interpolate = interpolate; FrameWidth = fw; FrameHeight = fh; Columns = columns; ImageFrames = imageFrames;
            foreach (var f in frames) CycleTicks += f.Time;
        }

        /// <summary>Parses an mcmeta file for an image of the given size. Null when there is no animation section, the
        /// frame size doesn't fit the image, or the result shows a single image frame only (nothing to animate).</summary>
        public static TextureAnimation Parse(string mcmetaJson, int imageWidth, int imageHeight)
        {
            if (string.IsNullOrEmpty(mcmetaJson) || imageWidth <= 0 || imageHeight <= 0) return null;
            Dictionary<string, object> anim;
            try { anim = (Json.Parse(mcmetaJson) as Dictionary<string, object>)?.O("animation"); }
            catch (System.Exception) { return null; }
            if (anim == null) return null;

            int fw, fh;
            bool hasW = anim.ContainsKey("width"), hasH = anim.ContainsKey("height");
            if (hasW) { fw = anim.I("width"); fh = hasH ? anim.I("height") : imageHeight; }
            else if (hasH) { fw = imageWidth; fh = anim.I("height"); }
            else fw = fh = System.Math.Min(imageWidth, imageHeight);
            if (fw <= 0 || fh <= 0 || imageWidth % fw != 0 || imageHeight % fh != 0) return null;
            int columns = imageWidth / fw, count = columns * (imageHeight / fh);

            int frametime = System.Math.Max(1, anim.I("frametime", 1));
            var frames = new List<Frame>();
            var list = anim.A("frames");
            if (list != null)
            {
                foreach (var e in list)
                {
                    int index, time = frametime;
                    if (e is double d) index = (int)d;
                    else if (e is Dictionary<string, object> o && o.ContainsKey("index")) { index = o.I("index"); time = System.Math.Max(1, o.I("time", frametime)); }
                    else continue;
                    if (index >= 0 && index < count) frames.Add(new Frame { Index = index, Time = time });   // vanilla drops bad indices
                }
            }
            else for (int i = 0; i < count; i++) frames.Add(new Frame { Index = i, Time = frametime });
            if (frames.Count == 0) return null;
            bool distinct = false;
            foreach (var f in frames) if (f.Index != frames[0].Index) { distinct = true; break; }
            if (!distinct) return null;
            return new TextureAnimation(frames.ToArray(), anim.B("interpolate"), fw, fh, columns, count);
        }

        /// <summary>State at a game tick: the image frame shown, the one after it, and the blend toward it
        /// (0 unless interpolating). Vanilla: frame i shows for Time ticks starting at the sum of earlier times.</summary>
        public void At(long tick, out int frame, out int next, out float blend)
        {
            long t = tick % CycleTicks;
            if (t < 0) t += CycleTicks;
            int i = 0;
            while (t >= Frames[i].Time) { t -= Frames[i].Time; i++; }
            frame = Frames[i].Index;
            next = Frames[(i + 1) % Frames.Length].Index;
            blend = Interpolate && next != frame ? (float)t / Frames[i].Time : 0f;
        }

        /// <summary>Top-left pixel of an image frame (image coordinates, y down).</summary>
        public void Origin(int imageFrame, out int x, out int y) { x = imageFrame % Columns * FrameWidth; y = imageFrame / Columns * FrameHeight; }

        /// <summary>Width and height from a PNG header (IHDR), without decoding the image.</summary>
        public static bool PngSize(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (png == null || png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P' || png[12] != (byte)'I' || png[13] != (byte)'H') return false;
            width = png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19];
            height = png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23];
            return width > 0 && height > 0;
        }
    }
}
