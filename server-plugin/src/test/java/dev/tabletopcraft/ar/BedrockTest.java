package dev.tabletopcraft.ar;

import org.junit.jupiter.api.Test;

import java.util.HashMap;
import java.util.Map;
import java.util.UUID;

import static org.junit.jupiter.api.Assertions.*;

/** [bedrock] Geyser/Floodgate players: config mode, the giant's Bedrock Mannequin size, skins that arrive late. */
class BedrockTest {
    @Test void modeIsAutoUnlessSwitchedOff() {
        assertEquals(BedrockSupport.Mode.AUTO, BedrockSupport.Mode.parse("auto"));
        assertEquals(BedrockSupport.Mode.AUTO, BedrockSupport.Mode.parse(null));
        assertEquals(BedrockSupport.Mode.AUTO, BedrockSupport.Mode.parse("something"));
        assertEquals(BedrockSupport.Mode.OFF, BedrockSupport.Mode.parse("off"));
        assertEquals(BedrockSupport.Mode.OFF, BedrockSupport.Mode.parse(" OFF "));
        assertEquals(BedrockSupport.Mode.OFF, BedrockSupport.Mode.parse("false"));   // YAML "off" read as a boolean
    }

    @Test void bedrockBodyNeedsAutoAndABridge() {
        assertTrue(BedrockSupport.wantBody(BedrockSupport.Mode.AUTO, true));
        assertFalse(BedrockSupport.wantBody(BedrockSupport.Mode.AUTO, false));   // no Floodgate or Geyser installed
        assertFalse(BedrockSupport.wantBody(BedrockSupport.Mode.OFF, true));
    }

    @Test void bedrockMannequinHeadMatchesTheFloatingHeadWithinTheCap() {
        // 0.25 m head at 0.01 m/block = 25 blocks; a Mannequin head is 0.5 blocks x scale, capped at 16 (8 blocks).
        var sizes = GiantPlacement.floatingSizes(0.01, 0.25, 0.1, 64);
        assertEquals(16.0, GiantPlacement.bedrockScale(sizes.headBlocks(), 16.0), 1e-9);
        // 0.1 m/block: a 2.5-block head -> scale 5, feet 1.62 x 5 blocks below the eyes.
        assertEquals(5.0, GiantPlacement.bedrockScale(GiantPlacement.floatingSizes(0.1, 0.25, 0.1, 64).headBlocks(), 16.0), 1e-9);
        assertEquals(8.0, GiantPlacement.bedrockScale(25, 8.0), 1e-9);              // max-scale below the attribute cap
        assertEquals(0.0625, GiantPlacement.bedrockScale(0.001, 16.0), 1e-9);       // the attribute's minimum
    }

    @Test void skinIsSentFirstAndAgainWhenItChanges() {
        Map<UUID, String> sent = new HashMap<>();
        UUID bedrock = new UUID(0, 0x0009_0000_1234_5678L);   // Floodgate's xuid-based UUID shape
        String url = "http://textures.minecraft.net/texture/abc";
        assertTrue(SkinLookup.changed(sent, bedrock, null));    // first listing: "skin": null (default skin)
        assertFalse(SkinLookup.changed(sent, bedrock, null));
        assertTrue(SkinLookup.changed(sent, bedrock, url));     // Floodgate applied the converted skin
        assertFalse(SkinLookup.changed(sent, bedrock, url));
        UUID java = UUID.randomUUID();
        assertTrue(SkinLookup.changed(sent, java, url));        // per player
        assertFalse(SkinLookup.changed(sent, java, url));
    }
}
