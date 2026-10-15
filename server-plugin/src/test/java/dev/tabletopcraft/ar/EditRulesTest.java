package dev.tabletopcraft.ar;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/** Block-edit rules on state strings (EditRules): replaceable cells, two-block partners, water, labels (PROTOCOL.md 4.20). */
class EditRulesTest {
    @Test void labels() {
        assertEquals("oak stairs", EditRules.label("minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]"));
        assertEquals("command block", EditRules.label("minecraft:command_block"));
        assertEquals("stone", EditRules.label("stone"));
        assertEquals("a cow", EditRules.article("cow"));
        assertEquals("an armadillo", EditRules.article("armadillo"));
    }

    @Test void properties() {
        String door = "minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]";
        assertEquals("lower", EditRules.prop(door, "half"));
        assertNull(EditRules.prop(door, "type"));
        assertNull(EditRules.prop("minecraft:stone", "half"));
        assertEquals("minecraft:oak_door[facing=north,half=upper,hinge=left,open=false,powered=false]", EditRules.withProp(door, "half", "upper"));
        assertEquals("minecraft:stone", EditRules.withProp("minecraft:stone", "half", "upper"));
    }

    @Test void replaceableLikeVanilla() {
        assertTrue(EditRules.replaceable("minecraft:short_grass", true, "minecraft:stone"));
        assertFalse(EditRules.replaceable("minecraft:short_grass", true, "minecraft:short_grass"), "a block doesn't replace itself");
        assertTrue(EditRules.replaceable("minecraft:snow[layers=1]", true, "minecraft:stone"));
        assertFalse(EditRules.replaceable("minecraft:snow[layers=3]", true, "minecraft:stone"), "Paper flags every snow layer replaceable; vanilla only a single one");
        assertTrue(EditRules.replaceable("minecraft:sculk_vein[down=true,east=false,north=false,south=false,up=false,waterlogged=false,west=false]", false, "minecraft:stone"),
            "multiface sculk vein is replaced by other blocks");
        assertFalse(EditRules.replaceable("minecraft:stone", false, "minecraft:dirt"));
        assertTrue(EditRules.replaceable("minecraft:water[level=0]", true, "minecraft:oak_slab[type=bottom,waterlogged=false]"));
        assertFalse(EditRules.replaceable("minecraft:oak_slab[type=bottom,waterlogged=false]", false, "minecraft:oak_slab[type=bottom,waterlogged=false]"));
    }

    @Test void waterSources() {
        assertTrue(EditRules.holdsWaterSource("minecraft:water[level=0]"));
        assertFalse(EditRules.holdsWaterSource("minecraft:water[level=3]"));
        assertFalse(EditRules.holdsWaterSource("minecraft:water[level=8]"), "falling water isn't a source");
        assertTrue(EditRules.holdsWaterSource("minecraft:oak_slab[type=bottom,waterlogged=true]"));
        assertTrue(EditRules.holdsWaterSource("minecraft:seagrass"));
        assertTrue(EditRules.holdsWaterSource("minecraft:tall_seagrass[half=upper]"));
        assertFalse(EditRules.holdsWaterSource("minecraft:air"));
        assertFalse(EditRules.holdsWaterSource("minecraft:oak_slab[type=bottom,waterlogged=false]"));
    }

    @Test void twoBlockPartners() {
        EditRules.Partner p = EditRules.partner("minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]");
        assertEquals(new EditRules.Partner(0, 1, 0, "minecraft:oak_door[facing=north,half=upper,hinge=left,open=false,powered=false]"), p);
        assertEquals(new EditRules.Partner(0, -1, 0, "minecraft:tall_grass[half=lower]"), EditRules.partner("minecraft:tall_grass[half=upper]"));
        assertEquals(new EditRules.Partner(1, 0, 0, "minecraft:red_bed[facing=east,occupied=false,part=head]"), EditRules.partner("minecraft:red_bed[facing=east,occupied=false,part=foot]"));
        assertEquals(new EditRules.Partner(0, 0, 1, "minecraft:red_bed[facing=north,occupied=false,part=foot]"), EditRules.partner("minecraft:red_bed[facing=north,occupied=false,part=head]"));
        assertEquals(0, EditRules.partner("minecraft:small_dripleaf[facing=north,half=lower,waterlogged=false]").dx());
        assertNull(EditRules.partner("minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]"), "stairs are one block");
        assertNull(EditRules.partner("minecraft:oak_trapdoor[facing=north,half=top,open=false,powered=false,waterlogged=false]"));
        assertNull(EditRules.partner("minecraft:pitcher_crop[age=0,half=lower]"), "the pitcher crop grows its top itself");
        assertNull(EditRules.partner("minecraft:stone"));
    }

    @Test void placedByLowerHalfOrFoot() {
        assertEquals("minecraft:oak_door[facing=east,half=lower,hinge=right,open=false,powered=false]",
            EditRules.placedHalf("minecraft:oak_door[facing=east,half=upper,hinge=right,open=false,powered=false]"));
        assertEquals("minecraft:red_bed[facing=east,occupied=false,part=foot]", EditRules.placedHalf("minecraft:red_bed[facing=east,occupied=false,part=head]"));
        assertEquals("minecraft:sunflower[half=lower]", EditRules.placedHalf("minecraft:sunflower[half=upper]"));
        assertEquals("minecraft:oak_stairs[facing=east,half=top,shape=straight,waterlogged=false]",
            EditRules.placedHalf("minecraft:oak_stairs[facing=east,half=top,shape=straight,waterlogged=false]"));
    }
}
