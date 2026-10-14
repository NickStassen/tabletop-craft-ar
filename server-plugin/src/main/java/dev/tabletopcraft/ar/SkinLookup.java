package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.util.UUID;
import java.util.regex.Pattern;

/**
 * Skin helpers. The giant's skin_player is resolved by Paper's profile lookup, which on an offline-mode server
 * maps a name to an offline UUID without textures; the name -> UUID step is then done here against the public
 * Mojang API, and Paper fetches the signed textures for that UUID. Pure parts are unit-tested (SkinLookupTest).
 */
final class SkinLookup {
    private SkinLookup() {}

    static final Pattern VALID_NAME = Pattern.compile("[A-Za-z0-9_]{1,16}");
    private static final HttpClient HTTP = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(5)).build();

    /** PROTOCOL.md model names: "slim" (3 px arms) or "wide" (4 px arms, Mojang's CLASSIC). */
    static String model(boolean slim) { return slim ? "slim" : "wide"; }

    /** Blocking (call off the main thread). Null when the name isn't a valid or existing Java account. */
    static UUID uuidForName(String name) throws IOException, InterruptedException {
        if (name == null || !VALID_NAME.matcher(name).matches()) return null;
        HttpRequest req = HttpRequest.newBuilder(URI.create("https://api.mojang.com/users/profiles/minecraft/" + name))
            .timeout(Duration.ofSeconds(10)).header("Accept", "application/json").GET().build();
        HttpResponse<String> res = HTTP.send(req, HttpResponse.BodyHandlers.ofString());
        return parseProfile(res.statusCode(), res.body());
    }

    /** {"id":"<32 hex>","name":"..."} with 200 -> UUID; 204/404 (no such account) -> null; other codes throw. */
    static UUID parseProfile(int status, String body) throws IOException {
        if (status == 204 || status == 404) return null;
        if (status != 200) throw new IOException("Mojang API HTTP " + status);
        JsonObject o = JsonParser.parseString(body).getAsJsonObject();
        return o.has("id") ? dashed(o.get("id").getAsString()) : null;
    }

    /**
     * Blocking (call off the main thread): downloads the skin PNG (textures.minecraft.net only, over https) and
     * returns the plain block closest to its hand colour, or null if it can't be read.
     */
    static String handBlock(java.net.URL skin, boolean slim) throws IOException, InterruptedException {
        if (skin == null || !"textures.minecraft.net".equalsIgnoreCase(skin.getHost())) return null;
        HttpRequest req = HttpRequest.newBuilder(URI.create("https://textures.minecraft.net" + skin.getPath())).timeout(Duration.ofSeconds(10)).GET().build();
        HttpResponse<byte[]> res = HTTP.send(req, HttpResponse.BodyHandlers.ofByteArray());
        if (res.statusCode() != 200) throw new IOException("skin download HTTP " + res.statusCode());
        java.awt.image.BufferedImage img = javax.imageio.ImageIO.read(new java.io.ByteArrayInputStream(res.body()));
        if (img == null) return null;
        int w = img.getWidth(), h = img.getHeight();
        int c = HandColour.handColour(img.getRGB(0, 0, w, h, null, 0, w), w, h, slim);
        return c < 0 ? null : HandColour.nearest(c);
    }

    /** "0123456789abcdef0123456789abcdef" -> UUID (Mojang returns ids without dashes). */
    static UUID dashed(String hex) {
        if (hex.length() != 32) return UUID.fromString(hex);
        return UUID.fromString(hex.substring(0, 8) + "-" + hex.substring(8, 12) + "-" + hex.substring(12, 16) + "-" + hex.substring(16, 20) + "-" + hex.substring(20));
    }
}
