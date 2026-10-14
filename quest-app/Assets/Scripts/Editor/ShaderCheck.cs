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
        static readonly string[] Shaders = { "TabletopCraft/Blocks", "TabletopCraft/BlocksTransparent", "TabletopCraft/PlayerSkin" };
        static readonly string[][] Keywords = { new string[0], new[] { "STEREO_MULTIVIEW_ON" }, new[] { "STEREO_INSTANCING_ON" }, new[] { "INSTANCING_ON" }, new[] { "UNITY_SINGLE_PASS_STEREO" } };

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
    }
}
#endif
