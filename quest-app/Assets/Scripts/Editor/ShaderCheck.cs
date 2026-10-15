// Compiles the project's block shaders for Android (Vulkan and GLES3) with the stereo/instancing keywords the Quest
// uses, without a graphics device or a player build, and prints any errors. Headless:
//   Unity -batchmode -nographics -quit -projectPath quest-app -buildTarget Android \
//     -executeMethod TabletopCraft.EditorTools.ShaderCheck.Run -logFile shaders.log
//   grep -E "SHADERCHECK" shaders.log
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace TabletopCraft.EditorTools
{
    public static class ShaderCheck
    {
        static readonly string[] Shaders = { "TabletopCraft/Blocks", "TabletopCraft/BlocksTransparent", "TabletopCraft/PlayerSkin", "TabletopCraft/Entity", "TabletopCraft/BlockEntityFx", "TabletopCraft/EndPortal",   // [blocks] the last two
                                             "TabletopCraft/Weather", "TabletopCraft/Lines", "TabletopCraft/Particles" };   // [living] rain/snow; leads, lines, bolts; [motion] particles
        static readonly string[][] Keywords = { new string[0], new[] { "STEREO_MULTIVIEW_ON" }, new[] { "STEREO_INSTANCING_ON" }, new[] { "INSTANCING_ON" }, new[] { "UNITY_SINGLE_PASS_STEREO" },
                                                // [gpu] the block shaders' variants (BlockKeywords, TabletopRenderer.SolidMaterial)
                                                new[] { "STEREO_MULTIVIEW_ON", "TABLETOP_SOLID", "TABLETOP_CLIP_PLANES" }, new[] { "STEREO_MULTIVIEW_ON", "TABLETOP_FOG", "TABLETOP_DAYLIGHT" },
                                                new[] { "STEREO_MULTIVIEW_ON", "TABLETOP_SOLID", "TABLETOP_FOG", "TABLETOP_DAYLIGHT", "TABLETOP_CLIP_PLANES" },
                                                new[] { "STEREO_MULTIVIEW_ON", "TABLETOP_PACKED", "TABLETOP_SOLID", "TABLETOP_CLIP_PLANES" },
                                                new[] { "STEREO_MULTIVIEW_ON", "TABLETOP_PACKED", "TABLETOP_FOG", "TABLETOP_DAYLIGHT" } };

        public static void Run()
        {
            int errors = 0, variants = 0;
            foreach (var name in Shaders)
            {
                var sh = Shader.Find(name);
                if (sh == null) { Debug.LogError("SHADERCHECK missing shader " + name); errors++; continue; }
                var data = ShaderUtil.GetShaderData(sh);
                for (int si = 0; si < data.SubshaderCount; si++)
                {
                    var sub = data.GetSubshader(si);
                    for (int pi = 0; pi < sub.PassCount; pi++)
                    {
                        var pass = sub.GetPass(pi);
                        foreach (var platform in new[] { ShaderCompilerPlatform.Vulkan, ShaderCompilerPlatform.GLES3x })
                            foreach (var kw in Keywords)
                                foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
                                {
                                    var r = pass.CompileVariant(stage, kw, platform, BuildTarget.Android, false);
                                    variants++;
                                    foreach (var m in r.Messages)
                                        if (m.severity == ShaderCompilerMessageSeverity.Error)
                                        {
                                            errors++;
                                            Debug.LogError($"SHADERCHECK ERROR {name} pass {pi} {platform} {stage} [{string.Join(" ", kw)}]: {m.message} ({m.file}:{m.line})");
                                        }
                                    if (!r.Success && r.Messages.Length == 0) { errors++; Debug.LogError($"SHADERCHECK ERROR {name} {platform} {stage} [{string.Join(" ", kw)}] failed"); }
                                }
                    }
                }
            }
            Debug.Log($"SHADERCHECK done: {variants} variants, {errors} errors");
            if (Application.isBatchMode) EditorApplication.Exit(errors == 0 ? 0 : 1);
        }

        /// <summary>
        /// [gpu] Cost of the block shaders per variant, for comparing changes offline (no Adreno compiler here): GLES3
        /// statements in main (HLSLcc emits about one per DXBC instruction), vertex outputs (varying components, the
        /// clip position included), vertex inputs; the Vulkan compile must succeed too. Headless:
        ///   Unity ... -executeMethod TabletopCraft.EditorTools.ShaderCheck.Stats -logFile stats.log; grep SHADERSTATS stats.log
        /// With TABLETOP_SHADER_DUMP=dir the GLSL of each variant is written there.
        /// </summary>
        public static void Stats()
        {
            string dump = System.Environment.GetEnvironmentVariable("TABLETOP_SHADER_DUMP");
            foreach (var name in new[] { "TabletopCraft/Blocks", "TabletopCraft/BlocksTransparent" })
            {
                var sh = Shader.Find(name);
                var pass = ShaderUtil.GetShaderData(sh).GetSubshader(0).GetPass(0);
                foreach (var extra in StatKeywords)
                {
                    var kw = new System.Collections.Generic.List<string> { "STEREO_MULTIVIEW_ON" };
                    kw.AddRange(extra);
                    var line = new System.Text.StringBuilder($"SHADERSTATS {name.Substring(14)} [{string.Join(" ", extra)}]");
                    // GLES3: the vertex compile returns the whole program (#ifdef VERTEX ... #ifdef FRAGMENT ...).
                    var g = pass.CompileVariant(ShaderType.Vertex, kw.ToArray(), ShaderCompilerPlatform.GLES3x, BuildTarget.Android, false);
                    if (!g.Success) { Debug.Log(line + " compile failed"); continue; }
                    string glsl = System.Text.Encoding.UTF8.GetString(g.ShaderData);
                    int split = glsl.IndexOf("#ifdef FRAGMENT", System.StringComparison.Ordinal);
                    string vs = split > 0 ? glsl.Substring(0, split) : glsl, fs = split > 0 ? glsl.Substring(split) : "";
                    if (!string.IsNullOrEmpty(dump))
                    {
                        System.IO.Directory.CreateDirectory(dump);
                        System.IO.File.WriteAllText(System.IO.Path.Combine(dump, $"{name.Substring(14)}_{string.Join("_", extra)}.glsl"), glsl);
                    }
                    var vv = pass.CompileVariant(ShaderType.Vertex, kw.ToArray(), ShaderCompilerPlatform.Vulkan, BuildTarget.Android, false);
                    var vf = pass.CompileVariant(ShaderType.Fragment, kw.ToArray(), ShaderCompilerPlatform.Vulkan, BuildTarget.Android, false);
                    line.Append($" VS: glsl={Statements(vs)} in={Components(vs, "in")} out={Components(vs, "out") + 4}");
                    line.Append($" FS: glsl={Statements(fs)} tex={Count(fs, "texture")} discard={Count(fs, "discard")} clipdist={Count(vs, "gl_ClipDistance")}");
                    if (!vv.Success || !vf.Success) line.Append(" VULKAN COMPILE FAILED");
                    Debug.Log(line.ToString());
                }
            }
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>Keyword sets Stats compiles (STEREO_MULTIVIEW_ON is always on, as on the Quest).</summary>
        public static string[][] StatKeywords = { new string[0], new[] { "TABLETOP_CLIP_PLANES" }, new[] { "TABLETOP_SOLID" }, new[] { "TABLETOP_SOLID", "TABLETOP_CLIP_PLANES" },
                                                  new[] { "TABLETOP_DAYLIGHT", "TABLETOP_CLIP_PLANES" }, new[] { "TABLETOP_SOLID", "TABLETOP_FOG", "TABLETOP_DAYLIGHT", "TABLETOP_CLIP_PLANES" },
                                                  new[] { "TABLETOP_PACKED", "TABLETOP_SOLID", "TABLETOP_CLIP_PLANES" }, new[] { "TABLETOP_PACKED", "TABLETOP_DAYLIGHT", "TABLETOP_CLIP_PLANES" },
                                                  new[] { "TABLETOP_PACKED", "TABLETOP_CLIP_PLANES" } };

        static int Count(string s, string what)
        {
            int n = 0;
            for (int i = s.IndexOf(what, System.StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + what.Length, System.StringComparison.Ordinal)) n++;
            return n;
        }

        // Statements in main(): lines ending in ';' after "void main".
        static int Statements(string glsl)
        {
            int at = glsl.IndexOf("void main", System.StringComparison.Ordinal);
            if (at < 0) return -1;
            int n = 0;
            foreach (var l in glsl.Substring(at).Split('\n')) if (l.TrimEnd().EndsWith(";")) n++;
            return n;
        }

        // Scalar components of the global "in"/"out" declarations (vs_/ps_ prefixed by HLSLcc; gl_ built-ins excluded).
        static int Components(string glsl, string dir)
        {
            int n = 0;
            foreach (var raw in glsl.Split('\n'))
            {
                var l = raw.Trim();
                int k = l.IndexOf(dir + " ", System.StringComparison.Ordinal);
                if (k < 0 || !l.EndsWith(";") || l.Contains("(") || l.Contains("gl_")) continue;
                if (k > 0 && !(l.StartsWith("layout") || l.StartsWith("flat") || l.StartsWith("noperspective") || l.StartsWith("centroid"))) continue;
                var t = l.Substring(k + dir.Length + 1).Trim().Split(' ');
                if (t.Length < 2) continue;
                string ty = t.Length >= 3 && (t[0] == "highp" || t[0] == "mediump" || t[0] == "lowp") ? t[1] : t[0];
                n += ty.EndsWith("4") ? 4 : ty.EndsWith("3") ? 3 : ty.EndsWith("2") ? 2 : 1;
            }
            return n;
        }
    }
}
#endif
