// Minimal JSON reader/writer (objects -> Dictionary<string, object>, arrays -> List<object>, numbers -> double).
// JsonUtility cannot read the protocol's nested int arrays or polymorphic messages, so we keep this tiny parser.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TabletopCraft.Net
{
    public static class Json
    {
        public static object Parse(string s) { int i = 0; var v = Value(s, ref i); Ws(s, ref i); if (i != s.Length) throw new FormatException("trailing data"); return v; }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{') return Obj(s, ref i);
            if (c == '[') return Arr(s, ref i);
            if (c == '"') return Str(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            // [smooth] Plain integers (nearly every number: RLE runs, palette indices, light) without a substring, and the
            // small ones as shared boxes: a glide delta has hundreds of thousands of them, and their garbage drove the
            // headset's GC.
            bool neg = c == '-';
            int j = neg ? i + 1 : i, digits = 0;
            long v = 0;
            while (j < s.Length && s[j] >= '0' && s[j] <= '9' && digits < 16) { v = v * 10 + (s[j] - '0'); j++; digits++; }
            if (digits > 0 && digits < 16 && (j == s.Length || "+-.eE0123456789".IndexOf(s[j]) < 0) && !(neg && v == 0))
            {
                i = j;
                if (neg) v = -v;
                return v >= 0 && v < SmallInts.Length ? SmallInts[v] : (object)(double)v;
            }
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new FormatException("bad token at " + i);
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>Boxed 0..4096 (shared: a boxed double is immutable).</summary>
        static readonly object[] SmallInts = MakeSmallInts(4097);
        static object[] MakeSmallInts(int n) { var a = new object[n]; for (int k = 0; k < n; k++) a[k] = (double)k; return a; }

        static Dictionary<string, object> Obj(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; Ws(s, ref i);
            if (s[i] == '}') { i++; return d; }
            while (true)
            {
                Ws(s, ref i);
                string k = Str(s, ref i);
                Ws(s, ref i); if (s[i++] != ':') throw new FormatException("expected :");
                d[k] = Value(s, ref i);
                Ws(s, ref i);
                char c = s[i++];
                if (c == '}') return d;
                if (c != ',') throw new FormatException("expected , or }");
            }
        }

        static List<object> Arr(string s, ref int i)
        {
            var l = new List<object>();
            i++; Ws(s, ref i);
            if (s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(Value(s, ref i));
                Ws(s, ref i);
                char c = s[i++];
                if (c == ']') return l;
                if (c != ',') throw new FormatException("expected , or ]");
            }
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("expected string");
            i++;
            var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c == '\\')
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            i++;
            return sb.ToString();
        }

        public static string Write(object v) { var sb = new StringBuilder(); W(sb, v); return sb.ToString(); }

        static void W(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s:
                    sb.Append('"');
                    foreach (char c in s)
                    {
                        if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                        else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                    }
                    sb.Append('"'); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
                case int n: sb.Append(n); break;
                case long n: sb.Append(n); break;
                case uint n: sb.Append(n); break;
                case IDictionary<string, object> d:
                    sb.Append('{'); bool first = true;
                    foreach (var kv in d) { if (!first) sb.Append(','); first = false; W(sb, kv.Key); sb.Append(':'); W(sb, kv.Value); }
                    sb.Append('}'); break;
                case System.Collections.IEnumerable e:
                    sb.Append('['); bool f1 = true;
                    foreach (var x in e) { if (!f1) sb.Append(','); f1 = false; W(sb, x); }
                    sb.Append(']'); break;
                default: sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            }
        }

        // Typed accessors.
        public static string S(this Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as string : null;
        public static double D(this Dictionary<string, object> d, string k, double def = 0) => d.TryGetValue(k, out var v) && v is double x ? x : def;
        public static int I(this Dictionary<string, object> d, string k, int def = 0) => d.TryGetValue(k, out var v) && v is double x ? (int)x : def;
        public static bool B(this Dictionary<string, object> d, string k, bool def = false) => d.TryGetValue(k, out var v) && v is bool x ? x : def;
        public static List<object> A(this Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as List<object> : null;
        public static Dictionary<string, object> O(this Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
        public static int[] Ints(List<object> l) { var r = new int[l.Count]; for (int i = 0; i < r.Length; i++) r[i] = (int)(double)l[i]; return r; }
        public static double[] Doubles(List<object> l) { var r = new double[l.Count]; for (int i = 0; i < r.Length; i++) r[i] = (double)l[i]; return r; }
    }
}
