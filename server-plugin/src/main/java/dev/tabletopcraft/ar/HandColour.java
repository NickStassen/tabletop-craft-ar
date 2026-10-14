package dev.tabletopcraft.ar;

/**
 * Picks the block for the floating giant's hands: the plain block whose colour is closest to the hand on the
 * user's skin. Java players see a hand-sized, hand-shaped box in the user's own skin tone (or glove/sleeve colour),
 * which reads as "their hand" better than a fixed block, and a box keeps the controller's orientation visible.
 * A player-head item would put a face on each hand. Pure (unit-tested in HandColourTest).
 */
final class HandColour {
    private HandColour() {}

    record Block(String id, int rgb) {}

    /** Average colours of the 26.3 block textures (computed offline from the client jar; plain, opaque, cube items). */
    static final Block[] PALETTE = {
        new Block("white_concrete", 0xcfd5d6), new Block("white_terracotta", 0xd1b2a1), new Block("white_wool", 0xe9ecec),
        new Block("orange_concrete", 0xe06100), new Block("orange_terracotta", 0xa15325), new Block("orange_wool", 0xf07613),
        new Block("magenta_concrete", 0xa9309f), new Block("magenta_terracotta", 0x95586c), new Block("magenta_wool", 0xbd44b3),
        new Block("light_blue_concrete", 0x2389c6), new Block("light_blue_terracotta", 0x716c89), new Block("light_blue_wool", 0x3aafd9),
        new Block("yellow_concrete", 0xf0af15), new Block("yellow_terracotta", 0xba8523), new Block("yellow_wool", 0xf8c527),
        new Block("lime_concrete", 0x5ea818), new Block("lime_terracotta", 0x677534), new Block("lime_wool", 0x70b919),
        new Block("pink_concrete", 0xd5658e), new Block("pink_terracotta", 0xa14e4e), new Block("pink_wool", 0xed8dac),
        new Block("gray_concrete", 0x36393d), new Block("gray_terracotta", 0x392a23), new Block("gray_wool", 0x3e4447),
        new Block("light_gray_concrete", 0x7d7d73), new Block("light_gray_terracotta", 0x876a61), new Block("light_gray_wool", 0x8e8e86),
        new Block("cyan_concrete", 0x157788), new Block("cyan_terracotta", 0x565b5b), new Block("cyan_wool", 0x158991),
        new Block("purple_concrete", 0x641f9c), new Block("purple_terracotta", 0x764656), new Block("purple_wool", 0x792aac),
        new Block("blue_concrete", 0x2c2e8f), new Block("blue_terracotta", 0x4a3b5b), new Block("blue_wool", 0x35399d),
        new Block("brown_concrete", 0x603b1f), new Block("brown_terracotta", 0x4d3323), new Block("brown_wool", 0x724728),
        new Block("green_concrete", 0x495b24), new Block("green_terracotta", 0x4c532a), new Block("green_wool", 0x546d1b),
        new Block("red_concrete", 0x8e2020), new Block("red_terracotta", 0x8f3d2e), new Block("red_wool", 0xa02722),
        new Block("black_concrete", 0x080a0f), new Block("black_terracotta", 0x251610), new Block("black_wool", 0x141519),
        new Block("terracotta", 0x985e43),
        new Block("oak_planks", 0xa2824e), new Block("spruce_planks", 0x725430), new Block("birch_planks", 0xc0af79),
        new Block("jungle_planks", 0xa07350), new Block("acacia_planks", 0xa85a32), new Block("dark_oak_planks", 0x422b14),
        new Block("mangrove_planks", 0x753630), new Block("cherry_planks", 0xe2b2ac), new Block("pale_oak_planks", 0xe3d9d8),
        new Block("crimson_planks", 0x653046), new Block("warped_planks", 0x2b6863),
        new Block("smooth_stone", 0x9e9e9e), new Block("granite", 0x956755), new Block("polished_granite", 0x9a6a59),
        new Block("packed_mud", 0x8e6a4f), new Block("clay", 0xa0a6b3), new Block("mushroom_stem", 0xcbc4b9),
        new Block("dripstone_block", 0x866b5c), new Block("smooth_sandstone", 0xdfd6aa), new Block("smooth_red_sandstone", 0xb5611f),
        new Block("calcite", 0xdfe0dc), new Block("bone_block", 0xe5e1cf), new Block("raw_iron_block", 0xa6876b),
        new Block("raw_copper_block", 0x9a694f), new Block("honeycomb_block", 0xe5941d), new Block("mud", 0x3c393c),
    };

    /** The default (Steve) skin's average hand colour, for a giant without a resolved skin. */
    static final int DEFAULT_HAND_RGB = 0xa26a50;

    /** Closest palette block by the "redmean" weighted RGB distance (cheap and close to perceived difference). */
    static String nearest(int rgb) {
        int r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
        String best = PALETTE[0].id();
        double bestD = Double.MAX_VALUE;
        for (Block k : PALETTE) {
            int r2 = (k.rgb() >> 16) & 255, g2 = (k.rgb() >> 8) & 255, b2 = k.rgb() & 255;
            double rm = (r + r2) / 2.0, dr = r - r2, dg = g - g2, db = b - b2;
            double d = (2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db;
            if (d < bestD) { bestD = d; best = k.id(); }
        }
        return best;
    }

    /**
     * Average colour of the right hand on a 64x64 or legacy 64x32 skin (ARGB pixels, rows top-down): the bottom face
     * of the right arm and the lowest three rows around it. Alpha is ignored, as the game draws this base-layer area
     * opaque. -1 if the image isn't a skin.
     */
    static int handColour(int[] argb, int w, int h, boolean slim) {
        if (argb == null || w != 64 || (h != 32 && h != 64) || argb.length < w * h) return -1;
        int aw = slim ? 3 : 4;
        long r = 0, g = 0, b = 0, n = 0;
        // Right arm: texture offset (40, 16), depth 4, width aw, height 12. Bottom face: (44 + aw .. 44 + 2aw, 16..20).
        for (int y = 16; y < 32; y++)
            for (int x = 40; x < 48 + 2 * aw; x++) {
                boolean bottom = y < 20 && x >= 44 + aw && x < 44 + 2 * aw;
                boolean lowRows = y >= 29;
                if (!bottom && !lowRows) continue;
                int c = argb[y * w + x];
                r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255; n++;
            }
        return (int) (r / n) << 16 | (int) (g / n) << 8 | (int) (b / n);
    }
}
