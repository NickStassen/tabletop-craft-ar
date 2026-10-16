// Engine-free tests for the easy-setup work (1.1.0): the asset offer's state and note (World/AssetSync.cs).
using System;
using System.Collections.Generic;
using System.IO;
using TabletopCraft.Net;
using TabletopCraft.World;

static class SetupTests
{
    static Dictionary<string, object> Parse(string s) => Json.Parse(s) as Dictionary<string, object>;

    public static void Run(Action<bool, string> check, Func<string, string> fixture, string[] args)
    {
        AssetOfferNote(check, fixture);
    }

    static void AssetOfferNote(Action<bool, string> check, Func<string, string> fixture)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tabletop-setup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            var s = new AssetSync(dir);
            s.OnOffer(Parse(File.ReadAllText(fixture("assets_building.json"))));
            check(s.ServerState == "building" && s.ServerNote.Contains("26.3"), "setup: assets offer keeps state and note");
            check(s.Status.StartsWith("the server is preparing the game files") && s.Status.Contains("26.3"), "setup: building offer status: " + s.Status);
            var missing = Parse(File.ReadAllText(fixture("assets_building.json")));
            missing["state"] = "missing"; missing["note"] = "No Minecraft 26.3 on the server's PC";
            s.OnOffer(missing);
            check(s.Status == "the server offers nothing: No Minecraft 26.3 on the server's PC", "setup: missing offer status: " + s.Status);
            var old = new AssetSync(dir);
            old.OnOffer(Parse("{\"type\":\"assets\",\"chunk\":262144,\"files\":[]}"));
            check(old.ServerState == "" && old.Status == "the server offers nothing", "setup: a 1.0 offer (no state) still reads as before");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
