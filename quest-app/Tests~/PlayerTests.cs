// Engine-free tests for the player figures: model geometry and skin UVs, animation, skin image rules, and the
// player_positions parsing (PlayerModel.cs, PlayerAnimation.cs, SkinImage.cs, Protocol.cs PlayerInfo).
using System;
using System.Collections.Generic;
using System.Linq;
using TabletopCraft.Net;
using TabletopCraft.World;

static class PlayerTests
{
    public static void Run(Action<bool, string> check, Func<string, string> fixture)
    {
        Model(check);
        Animation(check);
        Skins(check);
        Parsing(check, fixture);
    }

    static (float x, float y, float z) P(FigureMesh m, int i) => (m.Pos[3 * i], m.Pos[3 * i + 1], m.Pos[3 * i + 2]);
    static (float x, float y, float z) N(FigureMesh m, int i) => (m.Normal[3 * i], m.Normal[3 * i + 1], m.Normal[3 * i + 2]);

    static void Model(Action<bool, string> check)
    {
        var wide = PlayerModel.Parts(false);
        check(wide.Length == PlayerModel.PartCount && wide.Select(p => (int)p.Part).SequenceEqual(Enumerable.Range(0, 6)), "player model: six parts in enum order");

        // Head: 2 boxes (base + hat) = 48 vertices, 72 indices; front face is the skin's (8..16, 8..16).
        var head = new FigureMesh();
        PlayerModel.BuildPart(wide[0], head);
        check(head.VertexCount == 48 && head.Tris.Count == 72, $"head mesh 48 verts / 72 indices (got {head.VertexCount}/{head.Tris.Count})");
        var front = Enumerable.Range(0, 24).Where(i => N(head, i).z > 0.5f).ToList();
        float umin = front.Min(i => head.Uv[2 * i]) * 64, umax = front.Max(i => head.Uv[2 * i]) * 64;
        float vmin = (1 - front.Max(i => head.Uv[2 * i + 1])) * 64, vmax = (1 - front.Min(i => head.Uv[2 * i + 1])) * 64;
        check(front.Count == 4 && umin == 8 && umax == 16 && vmin == 8 && vmax == 16, $"head front face uses skin (8..16, 8..16) (got u {umin}..{umax}, v {vmin}..{vmax})");
        // Seen from the front, the image's left column is on the character's right (+X), its top row at the top.
        int rightTop = front.OrderByDescending(i => P(head, i).x).ThenByDescending(i => P(head, i).y).First();
        check(head.Uv[2 * rightTop] * 64 == 8 && (1 - head.Uv[2 * rightTop + 1]) * 64 == 8, "head front: image top-left at the character's right eye corner");
        PlayerModel.Pivot(wide[0], out _, out float hpy, out _);
        float top = Enumerable.Range(0, 24).Max(i => P(head, i).y) + hpy;
        float hatTop = Enumerable.Range(24, 24).Max(i => P(head, i).y) + hpy;
        check(top == 32 && hatTop == 32.5f, $"head top at 32 px (2 blocks), hat 0.5 px out (got {top}, {hatTop})");
        // Every triangle faces outward with Unity's winding: cross(b - a, c - a) along the face normal.
        bool outward = true;
        for (int t = 0; t < head.Tris.Count; t += 3)
        {
            var a = P(head, head.Tris[t]); var b = P(head, head.Tris[t + 1]); var c = P(head, head.Tris[t + 2]);
            float ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z, vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
            float cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            var n = N(head, head.Tris[t]);
            if (cx * n.x + cy * n.y + cz * n.z <= 0) outward = false;
        }
        check(outward, "player model: all triangles wound outward for Unity");

        // Right arm on +X (the figure faces +Z), 4 px wide; slim 3 px; legs reach the feet.
        var arm = new FigureMesh();
        PlayerModel.BuildPart(wide[2], arm);
        PlayerModel.Pivot(wide[2], out float apx, out float apy, out _);
        float axMin = Enumerable.Range(0, 24).Min(i => P(arm, i).x) + apx, axMax = Enumerable.Range(0, 24).Max(i => P(arm, i).x) + apx;
        check(apx == 5 && apy == 22 && axMin == 4 && axMax == 8, $"wide right arm at x 4..8 px (got {axMin}..{axMax}, pivot {apx},{apy})");
        var slimArm = new FigureMesh();
        var slim = PlayerModel.Parts(true);
        PlayerModel.BuildPart(slim[2], slimArm);
        float sMin = Enumerable.Range(0, 24).Min(i => P(slimArm, i).x) + 5, sMax = Enumerable.Range(0, 24).Max(i => P(slimArm, i).x) + 5;
        check(sMin == 4 && sMax == 7 && slim[3].Base.W == 3 && slim[3].Base.U == 32 && slim[3].Base.V == 48, $"slim arms 3 px (right arm x {sMin}..{sMax})");
        var leg = new FigureMesh();
        PlayerModel.BuildPart(wide[4], leg);
        PlayerModel.Pivot(wide[4], out float lpx, out float lpy, out _);
        check(Math.Abs(lpx - 1.9f) < 1e-6 && lpy == 12 && Enumerable.Range(0, 24).Min(i => P(leg, i).y) + lpy == 0, "right leg pivot (1.9, 12), foot at y 0");
        // Overlay boxes use the second-layer skin areas.
        check(wide[1].Overlay.U == 16 && wide[1].Overlay.V == 32 && wide[3].Overlay.U == 48 && wide[5].Overlay.V == 48 && wide[0].Overlay.Grow == 0.5f, "overlay layer offsets (jacket, sleeves, pants, hat)");
    }

    static void Animation(Action<bool, string> check)
    {
        var a = new PlayerAnimation();
        PlayerPose p = default;
        for (int i = 0; i < 120; i++) p = a.Step(1 / 60f, 0, 0, 30, 10, false);
        check(Math.Abs(p.RightLegX) < 1e-3 && Math.Abs(p.LeftLegX) < 1e-3 && p.BodyYaw == 30 && p.HeadYaw == 0 && p.HeadPitch == 10, $"standing: legs still, body under head (leg {p.RightLegX:F3}, body {p.BodyYaw})");
        check(Math.Abs(p.RightArmZ) <= 0.1f * 57.3f && Math.Abs(p.RightArmX) <= 0.05f * 57.3f + 1e-3, "standing: only the idle arm bob");
        for (int i = 0; i < 60; i++) p = a.Step(1 / 60f, 0, 0, 100, 0, false);
        check(Math.Abs(p.BodyYaw - 50) < 1e-3 && Math.Abs(p.HeadYaw - 50) < 1e-3, $"head turned 70: body follows to 50 behind (body {p.BodyYaw}, head {p.HeadYaw})");

        // Walk east (+X) at 4.3 blocks/s for 2 s, looking east (Minecraft yaw -90).
        var w = new PlayerAnimation();
        float maxLeg = 0, minLeg = 0, armLeg = 0;
        for (int i = 0; i < 120; i++)
        {
            p = w.Step(1 / 60f, 4.3f / 60f, 0, -90, 0, false);
            if (i > 60) { maxLeg = Math.Max(maxLeg, p.RightLegX); minLeg = Math.Min(minLeg, p.RightLegX); armLeg += p.RightArmX * p.RightLegX; }
        }
        float expect = 1.4f * 0.86f * 57.2958f;
        check(Math.Abs(w.WalkSpeed - 0.86f) < 0.02f && maxLeg > expect * 0.9f && minLeg < -expect * 0.9f && maxLeg <= expect + 1, $"walking: legs swing +-{expect:F0} deg (got {minLeg:F0}..{maxLeg:F0}, speed {w.WalkSpeed:F2})");
        check(Math.Abs(p.BodyYaw + 90) < 1 && armLeg < 0, $"walking: body faces the walk direction (yaw {p.BodyYaw:F1}), arms counter-swing");
        // Walking backwards (moving west while looking east) keeps the body facing east.
        var back = new PlayerAnimation();
        for (int i = 0; i < 120; i++) p = back.Step(1 / 60f, -3f / 60f, 0, -90, 0, false);
        check(Math.Abs(p.BodyYaw + 90) < 1, $"walking backwards: body stays with the head (yaw {p.BodyYaw:F1})");

        p = new PlayerAnimation().Step(0.05f, 0, 0, 0, 0, true);
        check(p.Crouching && Math.Abs(p.BodyX - 28.65f) < 0.1f && p.HeadDy == -4.2f && p.LegDz == -4f && p.RootDy == -2f, "sneaking: body leans 0.5 rad, head down 4.2 px, legs back 4 px");
        check(PlayerAnimation.Wrap(190) == -170 && PlayerAnimation.Wrap(-180) == -180 && PlayerAnimation.Wrap(540) == -180, "wrap degrees");
    }

    static byte[] Px(byte[] img, int x, int y) => img.Skip((y * 64 + x) * 4).Take(4).ToArray();

    static void Skins(Action<bool, string> check)
    {
        // Java's UUID.hashCode and DefaultPlayerSkin.get (values from the JDK).
        check(SkinImage.TryJavaUuidHash("5627dd98-e6be-3c21-b8a8-e92344183641", out int h1) && h1 == 1277771483, $"java uuid hash (got {h1})");
        check(SkinImage.TryJavaUuidHash("80000000-0000-0000-0000-000000000001", out int h2) && h2 == -2147483647, $"java uuid hash negative (got {h2})");
        check(SkinImage.DefaultSkinPath("5627dd98-e6be-3c21-b8a8-e92344183641", out bool s1) == "assets/minecraft/textures/entity/player/wide/efe.png" && !s1, "default skin: offline Steve uuid -> wide efe");
        check(SkinImage.DefaultSkinPath("80000000-0000-0000-0000-000000000001", out _) .EndsWith("wide/zuri.png"), "default skin: negative hash -> floorMod 17 = wide zuri");
        check(SkinImage.DefaultSkinPath("00000000-0000-0000-0000-000000000000", out bool s0).EndsWith("slim/alex.png") && s0, "default skin: hash 0 -> slim alex");
        check(SkinImage.DefaultSkinPath("ffffffff-ffff-ffff-0000-00000000000f", out _) == SkinImage.SteveWidePath && SkinImage.DefaultSkinPath("0f1e", out _) == SkinImage.SteveWidePath, "default skin: hash 15 and bad uuid -> wide steve");

        check(SkinImage.NormalizeUrl("http://textures.minecraft.net/texture/abc123") == "https://textures.minecraft.net/texture/abc123", "skin url upgraded to https");
        check(SkinImage.NormalizeUrl("https://textures.minecraft.net.example.com/texture/x") == null && SkinImage.NormalizeUrl("https://example.com/texture/x") == null
              && SkinImage.NormalizeUrl("file:///sdcard/x.png") == null && SkinImage.NormalizeUrl(null) == null && SkinImage.NormalizeUrl("https://textures.minecraft.net/texture/") == null, "skin url: other hosts and schemes refused");

        // Legacy 64x32 skin: right leg copied (mirrored) to the left leg slot, solid hat layer cleared, base opaque.
        var legacy = new byte[64 * 32 * 4];
        for (int i = 0; i < legacy.Length; i += 4) { legacy[i] = 10; legacy[i + 3] = 255; }
        void Put(byte[] img, int x, int y, byte r) { int i = (y * 64 + x) * 4; img[i] = r; img[i + 3] = 255; }
        Put(legacy, 4, 20, 200);     // right leg front, first column
        Put(legacy, 44, 20, 120);    // right arm front, first column
        legacy[(20 * 64 + 2) * 4 + 3] = 0; // a transparent base pixel
        var img = SkinImage.Process(legacy, 64, 32);
        check(img != null && img.Length == 64 * 64 * 4, "legacy skin -> 64x64");
        check(Px(img, 23, 52)[0] == 200, $"legacy: right leg front column mirrored into the left leg (got {Px(img, 23, 52)[0]})");
        check(Px(img, 39, 52)[0] == 120, $"legacy: right arm front mirrored into the left arm (got {Px(img, 39, 52)[0]})");
        check(Px(img, 40, 4)[3] == 0 && Px(img, 2, 20)[3] == 255 && Px(img, 4, 40)[3] == 0, "legacy: solid hat cleared, base forced opaque, no second layer");
        var modern = new byte[64 * 64 * 4];
        modern[(8 * 64 + 40) * 4 + 3] = 255; // one opaque hat pixel
        var m2 = SkinImage.Process(modern, 64, 64);
        check(Px(m2, 40, 8)[3] == 255 && Px(m2, 41, 8)[3] == 0 && Px(m2, 10, 10)[3] == 255 && Px(m2, 4, 36)[3] == 0, "64x64: hat kept as is, base opaque, overlay transparent");
        check(SkinImage.Process(new byte[32 * 32 * 4], 32, 32) == null, "non-skin sizes rejected");

        var plain = SkinImage.PlainColours(SkinImage.PlainVariant("5627dd98-e6be-3c21-b8a8-e92344183641"));
        check(Px(plain, 12, 14)[3] == 255 && Px(plain, 12, 14)[0] == 198 && Px(plain, 40, 4)[3] == 0 && Px(plain, 20, 24)[3] == 255, "plain-colour skin: face, shirt, no hat layer");
    }

    static void Parsing(Action<bool, string> check, Func<string, string> fixture)
    {
        var m = Json.Parse(System.IO.File.ReadAllText(fixture("player_positions.json"))) as Dictionary<string, object>;
        var p = PlayerInfo.From((Dictionary<string, object>)m.A("players")[0]);
        check(p.Name == "Alex" && p.HasSkin && p.SkinModel == "slim" && p.Pos.x == 112.5f && p.Yaw == 135f && !p.Sneaking, "player_positions fixture -> PlayerInfo (skin_model slim)");
        var later = PlayerInfo.From(Json.Parse("{\"uuid\":\"u\",\"name\":\"B\",\"pos\":[1,2,3],\"yaw\":0,\"pitch\":0,\"sneaking\":true}") as Dictionary<string, object>);
        check(!later.HasSkin && later.Skin == null && later.SkinModel == null && later.Sneaking, "player_positions without skin keeps HasSkin false");
    }
}
