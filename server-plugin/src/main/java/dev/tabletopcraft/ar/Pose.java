package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;

/**
 * One tracked transform in the tabletop frame (PROTOCOL.md section 3): metres, axes parallel to the world
 * (+X east, +Y up, +Z south), origin at the region's local (0,0,0) corner. Quaternion [x,y,z,w];
 * identity faces -Z (north).
 */
public record Pose(double px, double py, double pz, float qx, float qy, float qz, float qw, boolean tracked) {
    public static final double MAX_METRES = 20.0;

    public static Pose parse(JsonElement el) {
        if (el == null || el.isJsonNull()) return null;
        JsonObject o = el.getAsJsonObject();
        JsonArray p = o.getAsJsonArray("p");
        if (p == null || p.size() != 3) throw new IllegalArgumentException("p must be [x,y,z]");
        double x = p.get(0).getAsDouble(), y = p.get(1).getAsDouble(), z = p.get(2).getAsDouble();
        if (!Double.isFinite(x) || !Double.isFinite(y) || !Double.isFinite(z)) throw new IllegalArgumentException("non-finite position");
        x = clamp(x); y = clamp(y); z = clamp(z);
        float qx = 0, qy = 0, qz = 0, qw = 1;
        JsonArray q = o.getAsJsonArray("q");
        if (q != null) {
            if (q.size() != 4) throw new IllegalArgumentException("q must be [x,y,z,w]");
            qx = q.get(0).getAsFloat(); qy = q.get(1).getAsFloat(); qz = q.get(2).getAsFloat(); qw = q.get(3).getAsFloat();
            double len = Math.sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (!(len > 1e-6) || !Double.isFinite(len)) { qx = qy = qz = 0; qw = 1; }
            else { qx /= len; qy /= len; qz /= len; qw /= len; }
        }
        boolean tracked = !o.has("tracked") || o.get("tracked").getAsBoolean();
        return new Pose(x, y, z, qx, qy, qz, qw, tracked);
    }

    private static double clamp(double v) { return Math.max(-MAX_METRES, Math.min(MAX_METRES, v)); }

    /** world = origin + p / blockSizeM */
    public double[] toWorld(double ox, double oy, double oz, double blockSizeM) {
        return new double[] { ox + px / blockSizeM, oy + py / blockSizeM, oz + pz / blockSizeM };
    }

    /** Look vector f = q * (0,0,-1). */
    public double[] forward() {
        // Rotate v=(0,0,-1) by unit quaternion q.
        double x = qx, y = qy, z = qz, w = qw;
        double fx = -(2 * (x * z + w * y));
        double fy = -(2 * (y * z - w * x));
        double fz = -(1 - 2 * (x * x + y * y));
        return new double[] { fx, fy, fz };
    }

    /** Minecraft yaw in degrees: atan2(-f.x, f.z). 0 = south (+Z), 90 = west. */
    public float yawDeg() { double[] f = forward(); return (float) Math.toDegrees(Math.atan2(-f[0], f[2])); }

    /** Minecraft pitch in degrees: -asin(f.y). -90 = up. */
    public float pitchDeg() { double[] f = forward(); return (float) Math.toDegrees(-Math.asin(Math.max(-1, Math.min(1, f[1])))); }
}
