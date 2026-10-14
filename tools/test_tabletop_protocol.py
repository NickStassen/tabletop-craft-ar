import json
import math
import pathlib

import pytest

from tabletop_protocol import (
    CAP_ACTIONS,
    CAP_BIOMES,
    CAP_EDIT,
    CAP_ENTITIES,
    CAP_LIGHT,
    REGISTRY_CATEGORIES,
    EntityMirror,
    Envelope,
    WorldMirror,
    block_edit,
    decode_light,
    decode_rle,
    parse_region,
    registry_id,
    registry_states,
    scripted_pose,
    validate,
)

FIX = pathlib.Path(__file__).parent / "fixtures"


def snap():
    return {"v": 1, "type": "chunk_snapshot", "seq": 0, "t": 0, "snapshot_id": 1, "world": "world",
            "origin": [10, 60, 20], "dims": [2, 2, 2], "order": "yzx", "palette": ["minecraft:air", "minecraft:stone"],
            "encoding": "rle_palette_v1", "data": [1, 1, 0, 7], "biomes": None, "light": None}


@pytest.mark.parametrize("path", sorted(FIX.glob("*.json")), ids=lambda p: p.stem)
def test_spec_fixtures_validate(path):
    validate(json.loads(path.read_text()))


def test_player_positions_skin_model():
    msg = json.loads((FIX / "player_positions.json").read_text())
    assert msg["players"][0]["skin_model"] == "slim"
    msg["players"][0]["skin_model"] = "huge"
    with pytest.raises(ValueError):
        validate(msg)
    del msg["players"][0]["skin_model"]
    validate(msg)  # optional field


def test_decode_pairs_index_then_run():
    assert decode_rle([0, 2, 1, 1], 3) == [0, 0, 1]
    with pytest.raises(ValueError):
        decode_rle([0, 2], 3)


def test_mirror_and_block_update():
    m = WorldMirror()
    m.apply_snapshot(snap())
    assert m.at_world(10, 60, 20) == "minecraft:stone"
    assert m.at_world(11, 61, 21) == "minecraft:air"
    upd = {"snapshot_id": 1, "palette_add": ["minecraft:gold_block"], "changes": [[1, 1, 1, 2]]}
    assert m.apply_block_update(upd)
    assert m.at_world(11, 61, 21) == "minecraft:gold_block"
    assert not m.apply_block_update({"snapshot_id": 0, "changes": []})


def test_spec_snapshot_fixture_decodes():
    m = WorldMirror()
    m.apply_snapshot(json.loads((FIX / "chunk_snapshot.json").read_text()))
    assert m.dims == (32, 64, 32)


def test_envelope_and_pose():
    e = Envelope()
    a, b = e.wrap("ping", id=1), e.wrap("ping", id=2)
    assert (a["seq"], b["seq"]) == (0, 1)
    validate(e.wrap("pose", **scripted_pose(1.0)))
    q = scripted_pose(0.7)["head"]["q"]
    assert math.isclose(sum(c * c for c in q), 1.0)


def test_parse_region_forms():
    assert parse_region("0,-64,0,16,64,16") == {"origin": [0, -64, 0], "dims": [16, 64, 16]}
    c = parse_region("c:0.5,70,-3,32,64,32,-64")
    assert c == {"center": [0.5, 70.0, -3.0], "size": [32, 64, 32], "y_origin": -64}
    with pytest.raises(ValueError):
        parse_region("1,2,3")


def test_sections_snapshot_and_delta():
    """sections_v1 (PROTOCOL.md 4.18): full fixture, then the delta fixture against it."""
    m = WorldMirror()
    assert m.apply_snapshot(json.loads((FIX / "chunk_snapshot_sections.json").read_text()))
    assert m.dims == (32, 16, 16) and m.view_margin == (8, 0, 0)
    assert m.at_world(3, 67, 9) == "minecraft:stone" and m.at_world(3, 68, 9).startswith("minecraft:grass_block")
    assert m.at_world(20, 63, 15) == "minecraft:stone" and m.at_world(20, 64, 0) == "minecraft:air"
    delta = json.loads((FIX / "chunk_snapshot_delta.json").read_text())
    assert m.apply_snapshot(delta)
    assert m.origin == (16, 60, 0) and m.sections_sent == 1
    assert m.at_world(20, 63, 15) == "minecraft:stone"          # copied from the base
    assert m.at_world(32, 60, 0).startswith("minecraft:oak_log")  # new section
    assert m.at_world(32, 60, 1) == "minecraft:air"
    with pytest.raises(ValueError):
        m.apply_snapshot(delta)  # base 8 is no longer the current snapshot


def test_resize_deltas_grow_and_shrink():
    """[area] PROTOCOL.md 4.25: a delta whose dims differ from the base (a section-aligned resize)."""
    full = json.loads((FIX / "chunk_snapshot_sections.json").read_text())
    m = WorldMirror()
    m.apply_snapshot(full)
    before = {(x, y, z): m.at_world(x, y, z) for x in range(0, 32) for y in range(60, 76) for z in range(0, 16)}
    assert m.apply_snapshot(json.loads((FIX / "chunk_snapshot_grow.json").read_text()))
    assert m.dims == (64, 16, 16) and m.origin == (-16, 60, 0) and m.sections_sent == 2
    assert all(m.at_world(*p) == s for p, s in before.items()), "the old region is copied unchanged"
    assert m.at_world(-16, 67, 0) == "minecraft:stone" and m.at_world(-16, 68, 0).startswith("minecraft:grass_block")
    assert m.at_world(47, 63, 15) == "minecraft:stone" and m.at_world(47, 64, 15) == "minecraft:air"
    m2 = WorldMirror()
    m2.apply_snapshot(full)
    assert m2.apply_snapshot(json.loads((FIX / "chunk_snapshot_shrink.json").read_text()))
    assert m2.dims == (16, 16, 16) and m2.sections_sent == 0
    assert all(m2.at_world(x, y, z) == before[(x, y, z)] for x in range(16, 32) for y in range(60, 76) for z in range(0, 16))
    # A delta that leaves out a section reaching past the base is refused.
    bad = json.loads((FIX / "chunk_snapshot_grow.json").read_text())
    bad["sections"] = bad["sections"][:1]
    m3 = WorldMirror()
    m3.apply_snapshot(full)
    with pytest.raises(ValueError):
        m3.apply_snapshot(bad)


def test_limits_carry_max_view_width():
    """[area] welcome/region_info limits and region_too_large carry max_view_width (PROTOCOL.md 4.25)."""
    assert json.loads((FIX / "welcome.json").read_text())["limits"]["max_view_width"] == 96
    assert json.loads((FIX / "region_info.json").read_text())["limits"]["max_view_width"] == 96
    assert json.loads((FIX / "error_region_too_large.json").read_text())["max_view_width"] == 96
    rq = json.loads((FIX / "region_request_resize.json").read_text())
    window = [d - 2 * m for d, m in zip(rq["dims"], rq["view_margin"], strict=True)]
    assert window[0] == window[2] == 320


def test_sections_parts_wait_for_the_last_one():
    full = json.loads((FIX / "chunk_snapshot_sections.json").read_text())
    a, b = dict(full), dict(full)
    a["parts"] = b["parts"] = 2
    b["part"] = 1
    a["sections"], b["sections"] = full["sections"][:1], full["sections"][1:]
    m = WorldMirror()
    assert not m.apply_snapshot(a)
    assert m.apply_snapshot(b) and m.count("minecraft:stone") == 2048 + 1024


def test_sections_validation():
    full = json.loads((FIX / "chunk_snapshot_sections.json").read_text())
    validate(full)
    broken = dict(full)
    del broken["sections"]
    with pytest.raises(ValueError):
        validate(broken)
    short = dict(full, sections=full["sections"][:1])
    with pytest.raises(ValueError):
        WorldMirror().apply_snapshot(short)


def test_snapshot_extras_light_and_biomes():
    m = WorldMirror()
    m.apply_snapshot(json.loads((FIX / "chunk_snapshot_extras.json").read_text()))
    assert m.light is not None and len(m.light) == 32
    assert m.light_at_world(104, 63, -39) == (15, 14)   # local (2, 1, 3)
    assert m.light_at_world(102, 62, -42) == (0, 0)     # inside the grass block
    assert m.biomes.at_world(102, 62, -42) == "minecraft:plains"
    assert m.biomes.at_world(105, 63, -39) == "minecraft:swamp"   # quart (26, 15, -10)
    assert m.biomes.climate[1] == [0.8, 0.9]


def test_block_update_light_applies_and_old_snapshots_ignore_it():
    m = WorldMirror()
    m.apply_snapshot(json.loads((FIX / "chunk_snapshot_extras.json").read_text()))
    assert m.apply_block_update(json.loads((FIX / "block_update_light.json").read_text()))
    assert m.at_world(103, 63, -41) == "minecraft:torch"
    assert m.light_at_world(103, 63, -41) == (15, 14)
    assert m.apply_block_update({"snapshot_id": 7, "changes": [], "light": [[0, 1, 0, 17]]})
    assert m.light_at_world(102, 63, -42) == (1, 1)
    v1 = WorldMirror()
    v1.apply_snapshot(snap())   # light: null -> no light mirror, light entries are ignored
    assert v1.light is None and v1.biomes is None
    assert v1.apply_block_update({"snapshot_id": 1, "changes": [], "light": [[0, 0, 0, 240]]})


def test_decode_light_rejects_bad_data():
    assert decode_light({"encoding": "rle_v1", "data": [240, 2, 0, 1]}, 3) == [240, 240, 0]
    with pytest.raises(ValueError):
        decode_light({"encoding": "rle_v2", "data": [0, 3]}, 3)
    with pytest.raises(ValueError):
        decode_light({"encoding": "rle_v1", "data": [0, 2]}, 3)
    with pytest.raises(ValueError):
        decode_light({"encoding": "rle_v1", "data": [256, 3]}, 3)


def test_caps_in_hello_and_welcome_fixtures():
    assert {CAP_LIGHT, CAP_BIOMES} <= set(json.loads((FIX / "hello.json").read_text())["caps"])
    assert json.loads((FIX / "welcome.json").read_text())["caps"] == [CAP_LIGHT, CAP_BIOMES]


def test_entities_full_then_delta():
    m = EntityMirror()
    m.apply(json.loads((FIX / "entities.json").read_text()))
    assert len(m.entities) == 6 and m.fulls == 1
    boat = m.of_type("minecraft:oak_boat")[0]
    pig = m.entities[102]
    assert boat["passengers"] == [7, 102] and pig["vehicle"] == 101 and pig["baby"]
    assert m.entities[105]["item"] == "minecraft:diamond" and m.entities[105]["count"] == 3
    assert m.entities[104]["equipment"]["head"] == "minecraft:leather_helmet"
    m.apply(json.loads((FIX / "entities_delta.json").read_text()))
    assert 105 not in m.entities                                 # removed (picked up)
    assert m.entities[101]["pos"] == [113.0, 62.53, -30.25] and m.entities[101]["yaw"] == 95.0
    assert m.entities[101]["passengers"] == [7, 102]             # unchanged fields are kept
    assert m.entities[104]["equipment"] == {"main_hand": "minecraft:iron_sword"}   # replaced whole
    m.apply({"full": True, "entities": []})
    assert m.entities == {}


def test_entities_armor_stands():
    """Armour stands (PROTOCOL.md 4.21): arms, base_plate and stand_pose; a default stand sends neither arms nor a pose."""
    msg = json.loads((FIX / "entities_armor_stand.json").read_text())
    validate(msg)
    m = EntityMirror()
    m.apply(msg)
    plain, posed, small = m.entities[201], m.entities[202], m.entities[203]
    assert plain["base_plate"] and "arms" not in plain and "stand_pose" not in plain
    assert posed["arms"] and "base_plate" not in posed and posed["stand_pose"]["right_arm"] == [-110.0, 25.0, 10.0]
    assert small["small"] and small["invisible"] and small["equipment"] == {"head": "minecraft:iron_helmet"}
    m.apply({"full": False, "entities": [{"id": 202, "stand_pose": None, "arms": False}]})
    assert m.entities[202]["stand_pose"] is None and not m.entities[202]["arms"]   # cleared, like other fields
    del msg["entities"][1]["stand_pose"]["left_leg"]
    with pytest.raises(ValueError):
        validate(msg)


def test_entities_validation():
    msg = json.loads((FIX / "entities.json").read_text())
    del msg["entities"][0]["type"]
    with pytest.raises(ValueError):
        validate(msg)   # a full entry needs a type
    d = json.loads((FIX / "entities_delta.json").read_text())
    d["entities"][2]["equipment"] = {"hat": "minecraft:carved_pumpkin"}
    with pytest.raises(ValueError):
        validate(d)
    with pytest.raises(ValueError):
        EntityMirror().apply({"full": False, "entities": [{"id": 9, "pos": [0, 0, 0]}]})


def test_player_positions_riding_extras():
    msg = json.loads((FIX / "player_positions_riding.json").read_text())
    p = msg["players"][0]
    assert p["vehicle"] == 101 and p["pose"] == "riding" and p["id"] == 7
    assert p["equipment"]["main_hand"] == "minecraft:diamond_sword"
    p["pose"] = "flying"
    with pytest.raises(ValueError):
        validate(msg)
    assert CAP_ENTITIES == "entities_v1"


def test_sections_with_light_and_biomes():
    """sections_v1 + light_v1/biomes_v1: per-section light, biomes on part 0, deltas keep the base light."""
    m = WorldMirror()
    full = json.loads((FIX / "chunk_snapshot_sections_light.json").read_text())
    assert m.apply_snapshot(full)
    assert m.light_at_world(0, 60, 0) == (0, 0) and m.light_at_world(0, 61, 0) == (15, 0) and m.light_at_world(15, 63, 15) == (15, 14)
    assert m.biomes.at_world(5, 61, 5) == "minecraft:plains"
    # A delta with no sections listed: blocks and light carried over (here: shift 0, same region).
    delta = dict(full, snapshot_id=4, base={"snapshot_id": 3, "shift": [0, 0, 0]}, sections=[], biomes=None)
    assert m.apply_snapshot(delta) and m.light_at_world(15, 63, 15) == (15, 14) and m.at_world(3, 60, 3) == "minecraft:stone"
    no_light = dict(full, sections=[{"s": [0, 0, 0], "data": [1, 256, 0, 768]}])
    with pytest.raises(ValueError):
        WorldMirror().apply_snapshot(no_light)   # light announced but a section has none


def test_actions_fixture_and_validation():
    msg = json.loads((FIX / "actions.json").read_text())
    kinds = [a["kind"] for a in msg["actions"]]
    assert kinds == ["swing", "dig", "break", "place", "hurt", "swing"] and CAP_ACTIONS == "actions_v1"
    for bad in ({"kind": "wave", "id": 1}, {"kind": "dig", "pos": [1, 2, 3], "stage": 10},
                {"kind": "swing", "id": 7, "hand": "left"}, {"kind": "break", "pos": [1.5, 2, 3], "block": "minecraft:stone"}):
        m = dict(msg, actions=[bad])
        with pytest.raises(ValueError):
            validate(m)


def test_block_edit_fixtures_and_builder():
    place = json.loads((FIX / "block_edit.json").read_text())
    brk = json.loads((FIX / "block_edit_break.json").read_text())
    env = Envelope()
    built = env.wrap("block_edit", **block_edit("place", (105, 64, -35), "minecraft:oak_log[axis=x]", "east"))
    validate(built)
    assert {k: built[k] for k in ("op", "pos", "state", "face")} == {k: place[k] for k in ("op", "pos", "state", "face")}
    assert set(env.wrap("block_edit", **block_edit("break", [104, 63, -35], face="up"))) == set(brk)
    err = json.loads((FIX / "error_edit_denied.json").read_text())
    assert err["code"] == "edit_denied" and err["pos"] == place["pos"] and err["op"] == "place"


@pytest.mark.parametrize("patch", [
    {"op": "explode"},
    {"pos": [1, 2]},
    {"pos": [1, 2.5, 3]},
    {"pos": [1, True, 3]},
    {"state": None},
    {"state": "  "},
    {"face": "sideways"},
])
def test_block_edit_rejects_bad_fields(patch):
    msg = json.loads((FIX / "block_edit.json").read_text())
    msg.update(patch)
    if msg.get("state") is None:
        del msg["state"]
    with pytest.raises(ValueError):
        validate(msg)


def test_block_edit_break_needs_no_state_and_edit_errors_are_known():
    msg = json.loads((FIX / "block_edit_break.json").read_text())
    validate(msg)
    del msg["face"]
    validate(msg)   # face is optional
    for code in ("edit_denied", "out_of_region", "bad_state", "rate_limited"):
        validate({"v": 1, "type": "error", "seq": 0, "t": 0, "code": code, "message": "x", "pos": [0, 0, 0]})
    assert CAP_EDIT == "edit_v1"


def test_block_registry_fixture():
    msg = json.loads((FIX / "block_registry.json").read_text())
    validate(msg)
    assert [c["id"] for c in msg["categories"]] == list(REGISTRY_CATEGORIES)
    states = registry_states(msg)
    assert len(states) == msg["count"] == 13
    assert states["minecraft:oak_log"] == "minecraft:oak_log[axis=y]"
    assert registry_id("minecraft:stone") == "minecraft:stone"
    assert msg["items"]["minecraft:redstone_wire"] == "minecraft:redstone"
    assert msg["variants"]["minecraft:wall_torch"] in states


@pytest.mark.parametrize("patch", [
    {"count": 12},
    {"categories": [{"id": "building", "blocks": ["minecraft:stone", "minecraft:stone"]}]},
    {"categories": [{"id": "building", "blocks": [""]}]},
    {"categories": [{"blocks": []}]},
    {"variants": {"minecraft:wall_torch": "minecraft:nothing"}},
    {"items": {"minecraft:wheat": 3}},
])
def test_block_registry_rejects_bad_fields(patch):
    msg = json.loads((FIX / "block_registry.json").read_text())
    msg.update(patch)
    with pytest.raises(ValueError):
        validate(msg)
