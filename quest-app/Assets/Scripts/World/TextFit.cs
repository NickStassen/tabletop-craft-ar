// [v1.2.1] Fitting status text into a panel rectangle: legacy TextMesh doesn't wrap, so long lines ran off the block
// menu's Info tab and the text grew over its buttons (headset session 2026-10-16). Engine-free (Tests~/TextFitTests.cs).
using System.Text;

namespace TabletopCraft.World
{
    public static class TextFit
    {
        /// <summary>Line height and character widths in units of TextMesh.characterSize (font size 48, the built-in
        /// font), measured on a Quest 3 screenshot of the Info tab at characterSize 0.00135: 16 px lines, a capital
        /// about 3.0 and anything else about 2.15 characterSizes wide.</summary>
        public const float LineFactor = 5.4f, UpperFactor = 3.0f, OtherFactor = 2.15f;

        public readonly struct Result
        {
            public readonly string Text;
            public readonly float Size;
            public readonly int Lines;
            public Result(string text, float size, int lines) { Text = text; Size = size; Lines = lines; }
        }

        /// <summary>Width of one line in characterSize units.</summary>
        public static float Width(string line)
        {
            float w = 0;
            foreach (char c in line) w += c >= 'A' && c <= 'Z' ? UpperFactor : OtherFactor;
            return w;
        }

        /// <summary>Word-wraps every line to at most <paramref name="width"/> characterSize units; a wrapped line
        /// continues indented by two spaces. A word longer than the width is cut.</summary>
        public static string Wrap(string text, float width, out int lines)
        {
            var sb = new StringBuilder(text.Length + 16);
            lines = 0;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd();
                bool first = true;
                while (true)
                {
                    string prefix = first ? "" : "  ";
                    string body = first ? line : line.TrimStart();
                    if (Width(prefix + body) <= width || body.Length == 0)
                    {
                        if (lines > 0) sb.Append('\n');
                        sb.Append(prefix).Append(body);
                        lines++;
                        break;
                    }
                    // fits = how many characters fit; cut at the last space among them, else (one long word) at fits
                    int fits = 0, space = -1;
                    float w = Width(prefix);
                    while (fits < body.Length)
                    {
                        w += body[fits] >= 'A' && body[fits] <= 'Z' ? UpperFactor : OtherFactor;
                        if (w > width) break;
                        if (body[fits] == ' ') space = fits;
                        fits++;
                    }
                    int cut = space > 0 ? space : System.Math.Max(1, fits);
                    if (lines > 0) sb.Append('\n');
                    sb.Append(prefix).Append(body.Substring(0, cut).TrimEnd());
                    lines++;
                    line = body.Substring(cut);
                    first = false;
                    if (line.Trim().Length == 0) break;
                }
            }
            return sb.ToString();
        }

        /// <summary>The text wrapped to <paramref name="widthM"/> metres at the largest characterSize up to
        /// <paramref name="size"/> whose lines fit <paramref name="heightM"/> (not below <paramref name="minSize"/>).</summary>
        public static Result Fit(string text, float widthM, float heightM, float size, float minSize)
        {
            text ??= "";
            float s = size;
            while (true)
            {
                string wrapped = Wrap(text, widthM / s, out int n);
                if (n * LineFactor * s <= heightM || s <= minSize) return new Result(wrapped, s, n);
                s = System.Math.Max(minSize, s * 0.93f);
            }
        }
    }
}
