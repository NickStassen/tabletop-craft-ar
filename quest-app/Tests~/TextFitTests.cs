// [v1.2.1] TextFit (World/TextFit.cs): the block menu's Info tab text stays inside its rectangle, and the taller Info layout.
using System;
using System.Linq;
using TabletopCraft.Net;
using TabletopCraft.World;

static class TextFitTests
{
    public static void Run(Action<bool, string> check)
    {
        string wrapped = TextFit.Wrap("POSES 7768  MESH 114k faces  ENTITIES 40  BLOCK ENTITIES 7  interact_denied: a text display can't be hurt (kill or remove it)", 120, out int n);
        var lines = wrapped.Split('\n');
        check(n == lines.Length && n >= 2 && lines.All(l => TextFit.Width(l) <= 120) && lines.Skip(1).All(l => l.StartsWith("  ")),
              "textfit: a long status line wraps inside the width, continuation lines indented");
        check(string.Join(" ", lines.Select(l => l.Trim())).Replace("  ", " ") == "POSES 7768 MESH 114k faces ENTITIES 40 BLOCK ENTITIES 7 interact_denied: a text display can't be hurt (kill or remove it)",
              "textfit: wrapping keeps every word");
        TextFit.Wrap(new string('x', 300), 50, out int cutLines);
        check(cutLines >= 6, "textfit: one long word is cut where the width runs out");
        check(TextFit.Wrap("a\n\nb", 50, out int keep) == "a\n\nb" && keep == 3, "textfit: empty lines stay");

        // The Info tab as on the headset (2026-10-16): about 22 status lines and the disclaimer
        string status = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"LINE{i} some status text with numbers 128x186x128 @ (80, 116, -32)  PACK yes  LIGHT smooth")) +
                        "\nPAIR paired as Quest 3 (46b1721b)\n" + SetupCard.Disclaimer;
        var m = new BlockMenuModel();
        m.SetTab(m.InfoTab);
        var L = m.Layout();
        var fit = TextFit.Fit(status, L.Info.W - 0.004f, L.Info.H - 0.003f, 0.0012f, 0.0007f);
        float bottom = L.Info.Y + 0.002f + fit.Lines * TextFit.LineFactor * fit.Size;
        float buttons = L.Items.Where(i => i.Part == MenuPart.HudToggle || i.Part == MenuPart.ResetView || i.Part == MenuPart.SoundToggle).Min(i => i.R.Y);
        check(bottom <= buttons && fit.Size >= 0.0009f && fit.Text.Split('\n').All(l => TextFit.Width(l) * fit.Size <= L.Info.W),
              $"info tab: 22 status lines fit above the buttons and inside the width (size {fit.Size:F5}, {fit.Lines} lines, bottom {bottom:F3} <= {buttons:F3})");
        check(L.Info.H > 0.17f, $"info tab: taller text area ({L.Info.H:F3} m)");
        var pair = SetupCard.PairText(PairCode.From(Json.Parse("{\"code\":\"482913\",\"expires_s\":300,\"required\":true}") as System.Collections.Generic.Dictionary<string, object>, 0), 1, "10.0.0.5");
        check(pair.Contains("NOT AN OFFICIAL MINECRAFT PRODUCT.") && pair.Contains("NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT."), "pair card: shows the disclaimer");
        check(TabletopCraft.Net.PluginSupport.Named("1.1.0") == "v1.1.0" && TabletopCraft.Net.PluginSupport.Named("v1.2.0") == "v1.2.0", "versions are named vX.Y.Z");
    }
}
