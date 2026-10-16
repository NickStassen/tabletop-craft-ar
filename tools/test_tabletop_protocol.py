import json
import math
import pathlib

import pytest

from tabletop_protocol import (
    CAP_ACTIONS,
    CAP_BIOMES,
    CAP_BLOCK_ENTITIES,
    CAP_CHAT,
    CAP_COMMAND,
    CAP_EDIT,
    CAP_ENTITIES,
    CAP_INTERACT,
    CAP_LIGHT,
    CAP_MARKS,
    CAP_POWER,
    CAP_STATUS,
    CHAT_KINDS,
    DISCOVERY_PORT,
    ENTITY_OPS,
    ITEM_CATEGORIES,
    LIGHTNING_ITEM,
    POWER_LIMITS,
    REGISTRY_CATEGORIES,
    BlockEntityMirror,
    EntityMirror,
    Envelope,
    WorldMirror,
    block_edit,
    bookmark,
    box_edit_msg,
    brush_msg,
    container_edit,
    container_slots,
    decode_light,
    decode_rle,
    entity_op,
    interact,
    parse_region,
    registry_id,
    registry_states,
    scripted_pose,
    suggestion_apply,
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


def test_block_entities_fixture_and_mirror():
    msg = json.loads((FIX / "block_entities.json").read_text())
    validate(msg)
    m = BlockEntityMirror()
    m.entries[(100, 64, -35)] = {"kind": "sign"}
    m.apply(msg)
    assert CAP_BLOCK_ENTITIES == "block_entities_v1" and len(m.entries) == 12 and (100, 64, -35) not in m.entries
    sign = m.entries[(106, 64, -35)]
    assert sign["front"]["glowing"] and sign["front"]["color"] == "light_blue" and sign["back"]["lines"][0] == "Closed"
    assert [p["pattern"] for p in m.of_kind("banner")[0]["patterns"]] == ["minecraft:stripe_bottom", "minecraft:creeper"]
    assert m.of_kind("campfire")[0]["items"] == ["minecraft:beef", None, None, "minecraft:porkchop"]
    m.prune((100, 0, -40), (10, 256, 10))
    assert len(m.entries) == 5


def test_block_entities_reset_forgets_what_was_held():
    msg = json.loads((FIX / "block_entities_reset.json").read_text())
    validate(msg)
    m = BlockEntityMirror()
    m.apply(json.loads((FIX / "block_entities.json").read_text()))
    m.apply(msg)
    assert m.resets == 1 and list(m.entries) == [(-12, 70, 40)]
    with pytest.raises(ValueError):
        validate(dict(msg, reset="yes"))


@pytest.mark.parametrize("bad", [
    {"kind": "chest", "pos": [1, 2, 3]},
    {"kind": "sign", "pos": [1, 2, 3], "front": {"lines": ["a"], "color": "black", "glowing": False}},
    {"kind": "banner", "pos": [1, 2, 3], "patterns": [{"pattern": "minecraft:cross", "color": "mauve"}]},
    {"kind": "beacon", "pos": [1.5, 2, 3], "tier": 1},
    {"kind": "beacon", "pos": [1, 2, 3], "tier": 5},
    {"kind": "campfire", "pos": [1, 2, 3], "items": [None]},
    {"kind": "decorated_pot", "pos": [1, 2, 3], "sherds": ["minecraft:brick"]},
])
def test_block_entities_rejects_bad_entries(bad):
    msg = json.loads((FIX / "block_entities.json").read_text())
    with pytest.raises(ValueError):
        validate(dict(msg, entities=[bad]))


# ---------- [talk] chat, commands, status (PROTOCOL.md 4.26-4.28) ----------
CONTROL_FIXTURES = ("chat", "chat_death", "chat_operator", "chat_send", "command", "command_result", "command_output", "command_complete",
                    "command_suggestions", "server_status", "world_query", "world_info", "players_query", "players_info",
                    "error_command_denied", "hello_control", "welcome_control")


def test_control_fixtures_exist_and_validate():
    for name in CONTROL_FIXTURES:
        validate(json.loads((FIX / f"{name}.json").read_text()))


def test_control_caps_and_welcome_objects():
    hello = json.loads((FIX / "hello_control.json").read_text())
    welcome = json.loads((FIX / "welcome_control.json").read_text())
    assert {CAP_CHAT, CAP_COMMAND, CAP_STATUS} <= set(hello["caps"]) and hello["operator_name"] == "Nick"
    assert {CAP_CHAT, CAP_COMMAND, CAP_STATUS} <= set(welcome["caps"])
    assert welcome["chat"] == {"send": True, "name": "Nick", "max_len": 256}
    assert welcome["commands"]["max_len"] == 256 and "time set day" in welcome["commands"]["quick"]


def test_chat_kinds_and_from():
    for name in ("chat", "chat_death", "chat_operator"):
        m = json.loads((FIX / f"{name}.json").read_text())
        assert m["kind"] in CHAT_KINDS
    op = json.loads((FIX / "chat_operator.json").read_text())
    assert op["line"].endswith(op["text"]) and op["from"]["session"] == "6f1c"


def test_suggestions_replace_from_start():
    complete = json.loads((FIX / "command_complete.json").read_text())
    sug = json.loads((FIX / "command_suggestions.json").read_text())
    assert sug["ref_seq"] == complete["seq"]
    assert suggestion_apply(complete["line"], sug["start"], sug["items"][0]) == "gamerule advance_time"


def test_command_result_refers_to_the_command():
    cmd = json.loads((FIX / "command.json").read_text())
    res = json.loads((FIX / "command_result.json").read_text())
    assert res["ref_seq"] == cmd["seq"] and res["ok"] is True and cmd["at"]["pos"] == [105, 64, -35]


@pytest.mark.parametrize("name,patch", [
    ("chat", {"kind": "shout"}),
    ("chat", {"from": {"uuid": "x"}}),
    ("chat_send", {"text": ""}),
    ("chat_send", {"text": "x" * 257}),
    ("command", {"line": "time set day\nstop"}),
    ("command", {"line": " / "}),
    ("command", {"at": {"pos": [1, 2]}}),
    ("command_result", {"ok": "yes"}),
    ("command_result", {"lines": [1]}),
    ("command_suggestions", {"start": -1}),
    ("command_suggestions", {"items": ["x"] * 51}),
    ("server_status", {"tps": [20.0]}),
    ("world_info", {"weather": "snow"}),
    ("world_info", {"gamerules": {"x": [1]}}),
    ("players_info", {"players": [{"name": "A"}]}),
])
def test_control_validation_rejects(name, patch):
    msg = json.loads((FIX / f"{name}.json").read_text())
    msg.update(patch)
    with pytest.raises(ValueError):
        validate(msg)


def test_control_error_codes():
    for code in ("chat_denied", "command_denied"):
        validate({"v": 1, "type": "error", "seq": 0, "t": 0, "code": code, "message": "x"})


def test_discovery_probe_and_reply_fixtures():
    """LAN discovery (PROTOCOL.md 7): UDP probe and reply, no envelope."""
    probe = json.loads((FIX / "discovery_probe.json").read_text())
    reply = json.loads((FIX / "discovery_reply.json").read_text())
    validate(probe)
    validate(reply)
    assert probe == {"type": "tabletop_discover", "v": 1}
    assert reply["port"] == DISCOVERY_PORT and reply["path"] == "/tabletop" and reply["token"] is False


@pytest.mark.parametrize("patch", [{"port": 0}, {"port": 70000}, {"path": "tabletop"}, {"token": "no"}, {"v": 2}])
def test_discovery_reply_rejects_bad_fields(patch):
    msg = json.loads((FIX / "discovery_reply.json").read_text())
    msg.update(patch)
    with pytest.raises(ValueError):
        validate(msg)


def test_region_changed_carries_the_new_worlds_build_height():
    """next version: region_changed has limits (build_height of the region's world) for gliding after a world change."""
    msg = json.loads((FIX / "region_changed.json").read_text())
    validate(msg)
    assert msg["limits"]["build_height"] == [0, 256] and msg["world"] == "world_nether"
    msg["limits"]["build_height"] = [320, -64]
    with pytest.raises(ValueError):
        validate(msg)


# [hands] using blocks, items and mobs (PROTOCOL.md "Using blocks and items", "Acting on mobs")
INTERACT_FIXTURES = ("hello_interact", "welcome_interact", "interact", "interact_item", "item_registry", "entity_op", "entity_op_move",
                     "entity_op_use", "entity_op_name", "entity_info", "error_interact_denied", "error_not_found")


def test_interact_fixtures_exist_and_validate():
    for name in INTERACT_FIXTURES:
        validate(json.loads((FIX / f"{name}.json").read_text()))


def test_interact_caps_welcome_and_registry():
    hello = json.loads((FIX / "hello_interact.json").read_text())
    welcome = json.loads((FIX / "welcome_interact.json").read_text())
    reg = json.loads((FIX / "item_registry.json").read_text())
    assert CAP_INTERACT in hello["caps"] and CAP_INTERACT in welcome["caps"]
    assert welcome["interact"]["harm_players"] is False and welcome["interact"]["lightning"] in ("real", "effect", "off")
    assert [c["id"] for c in reg["categories"]] == list(ITEM_CATEGORIES)
    assert LIGHTNING_ITEM in reg["categories"][0]["items"] and "minecraft:bucket" in reg["categories"][0]["items"]


def test_interact_and_entity_op_builders_match_the_fixtures():
    env = Envelope()
    door = json.loads((FIX / "interact.json").read_text())
    built = env.wrap("interact", **interact((105, 64, -35), "north", hit=(0.5, 0.7, 0.0), dir=(0.1, -0.5, 0.86)))
    validate(built)
    keys = ("pos", "face", "item", "sneak", "hit", "dir")
    assert {k: built[k] for k in keys} == {k: door[k] for k in keys}
    move = json.loads((FIX / "entity_op_move.json").read_text())
    built = env.wrap("entity_op", **entity_op(move["uuid"], "move", to=[110.5, 64.0, -30.5], yaw=90.0))
    validate(built)
    assert {k: built[k] for k in ("uuid", "op", "to", "yaw")} == {k: move[k] for k in ("uuid", "op", "to", "yaw")}
    info = json.loads((FIX / "entity_info.json").read_text())
    assert info["uuid"] == move["uuid"] and info["entity_type"] == "minecraft:wolf"
    err = json.loads((FIX / "error_not_found.json").read_text())
    assert err["op"] == "entity_op" and err["uuid"] == move["uuid"]
    denied = json.loads((FIX / "error_interact_denied.json").read_text())
    assert denied["pos"] == json.loads((FIX / "interact_item.json").read_text())["pos"]


@pytest.mark.parametrize("name,patch", [
    ("interact", {"face": "sideways"}),
    ("interact", {"pos": [1, 2.5, 3]}),
    ("interact", {"item": "Bone Meal"}),
    ("interact", {"hit": [0.5, 3, 0.5]}),
    ("interact", {"dir": [1, 2]}),
    ("item_registry", {"count": 3}),
    ("item_registry", {"categories": [{"id": "use", "items": []}]}),
    ("entity_op", {"op": "explode"}),
    ("entity_op", {"uuid": "pig"}),
    ("entity_op_move", {"to": None}),
    ("entity_op_name", {"text": None}),
    ("entity_info", {"pos": [1, 2]}),
])
def test_interact_validation_rejects(name, patch):
    msg = json.loads((FIX / f"{name}.json").read_text())
    msg.update(patch)
    if name == "entity_op_name" and msg.get("text") is None:
        del msg["text"]
    with pytest.raises(ValueError):
        validate(msg)


def test_interact_error_codes():
    for code in ("interact_denied", "not_found"):
        validate({"v": 1, "type": "error", "seq": 0, "t": 0, "code": code, "message": "x"})
    for op in ENTITY_OPS:
        extra = {"to": [1.5, 64, 2.5]} if op in ("move", "hold") else {"text": ""} if op == "name" else {}
        validate(Envelope().wrap("entity_op", **entity_op("3c1e5f7a-2b4d-4e6f-8a9b-0c1d2e3f4a5b", op, **extra)))


# [power] undo/redo, box edits and brushes (PROTOCOL.md "Undo and redo", "Box edits", "Brushes")
POWER_FIXTURES = ("hello_power", "welcome_power", "block_edit_group", "interact_group", "undo", "redo", "history", "box_edit_fill",
                  "box_edit_replace", "box_edit_clone", "box_progress", "box_progress_done", "brush", "brush_replace", "error_box_denied")


def test_power_fixtures_exist_and_validate():
    for name in POWER_FIXTURES:
        validate(json.loads((FIX / f"{name}.json").read_text()))


def test_power_caps_and_limits():
    hello = json.loads((FIX / "hello_power.json").read_text())
    welcome = json.loads((FIX / "welcome_power.json").read_text())
    assert CAP_POWER in hello["caps"] and CAP_POWER in welcome["caps"] and CAP_EDIT in welcome["caps"]
    assert {k: welcome["limits"][k] for k in POWER_LIMITS} == {"undo_depth": 10000, "box_max_blocks": 32768, "brush_max_radius": 8,
                                                                "brush_blocks_per_second": 20000}
    del welcome["limits"]["undo_depth"]
    with pytest.raises(ValueError):
        validate(welcome)


def test_power_builders_match_the_fixtures():
    env = Envelope()
    fill = json.loads((FIX / "box_edit_fill.json").read_text())
    built = env.wrap("box_edit", **box_edit_msg("fill", fill["from"], fill["to"], state=fill["state"], group=fill["group"]))
    validate(built)
    keys = ("op", "from", "to", "state", "group")
    assert {k: built[k] for k in keys} == {k: fill[k] for k in keys}
    clone = json.loads((FIX / "box_edit_clone.json").read_text())
    built = env.wrap("box_edit", **box_edit_msg("clone", clone["from"], clone["to"], dest=clone["dest"], rotate=1, group=11))
    validate(built)
    assert built["dest"] == clone["dest"] and built["rotate"] == 1 and "state" not in built
    br = json.loads((FIX / "brush_replace.json").read_text())
    built = env.wrap("brush", **brush_msg("replace", br["center"], 4, "cylinder", state=br["state"], filter=br["filter"], height=1,
                                          group=13))
    validate(built)
    assert {k: built[k] for k in br if k not in ("seq", "t")} == {k: br[k] for k in br if k not in ("seq", "t")}
    done = json.loads((FIX / "box_progress_done.json").read_text())
    assert done["finished"] and done["changed"] + done["skipped"] + done["outside"] == done["processed"]


@pytest.mark.parametrize("name,patch", [
    ("block_edit_group", {"group": -1}),
    ("block_edit_group", {"group": 1.5}),
    ("interact_group", {"group": "eight"}),
    ("box_edit_fill", {"op": "melt"}),
    ("box_edit_fill", {"state": None}),
    ("box_edit_fill", {"from": [1, 2]}),
    ("box_edit_replace", {"filter": None}),
    ("box_edit_clone", {"rotate": 4}),
    ("box_edit_clone", {"dest": None}),
    ("box_progress", {"processed": 40000}),
    ("box_progress", {"op": "brush"}),
    ("box_progress_done", {"skipped_pos": [[1, 2]]}),
    ("brush", {"shape": "cube"}),
    ("brush", {"radius": -1}),
    ("brush", {"state": None}),
    ("brush_replace", {"filter": None}),
    ("history", {"undo": -1}),
])
def test_power_validation_rejects(name, patch):
    msg = json.loads((FIX / f"{name}.json").read_text())
    msg.update(patch)
    with pytest.raises(ValueError):
        validate(msg)


def test_world_state_fixture_and_rules():
    """[living] world_state (PROTOCOL.md "World state: time and weather"): a stormy midnight in the overworld."""
    msg = json.loads((FIX / "world_state.json").read_text())
    validate(msg)
    assert msg["day_time"] == 18000 and msg["rain"] == 1 and msg["thunder"] == 1 and msg["dimension"] == "overworld"
    for patch in ({"dimension": "nether"}, {"day_time": 24000}, {"rain": 0}, {"rain": 0.5}):
        bad = dict(msg, **patch)
        with pytest.raises(ValueError):
            validate(bad)


def test_entities_living_fields():
    """[living] leads, chests, orbs, bobbers, carried blocks, shulker lids, display entities and visible names."""
    msg = json.loads((FIX / "entities_living.json").read_text())
    validate(msg)
    m = EntityMirror()
    m.apply(msg)
    e = m.entities
    assert e[301]["leash"] == 7 and e[303]["leash"] == 302 and e[302]["type"] == "minecraft:leash_knot"
    assert e[304]["chested"] and e[304]["equipment"]["body"] == "minecraft:red_carpet"
    assert e[305]["xp"] == 17 and e[306]["owner"] == 7 and e[307]["carried"].startswith("minecraft:grass_block")
    assert e[308]["peek"] == 0.5 and e[308]["attach"] == "north" and e[313]["sheared"] and e[314]["name_visible"]
    text, item, block = e[309]["display"], e[310]["display"], e[311]["display"]
    assert text["billboard"] == "center" and text["text"].split("\n") == ["Welcome", "to the farm"] and text["background"] == 0x40000000
    assert item["item_transform"] == "fixed" and item["brightness"] == [15, 15]
    assert math.isclose(sum(q * q for q in item["left_rotation"]), 1, abs_tol=1e-3)
    assert block["scale"] == [0.5, 0.5, 0.5] and "text" not in block
    m.apply({"full": False, "entities": [{"id": 301, "leash": None}, {"id": 309, "display": None}]})
    assert e[301]["leash"] is None and e[309]["display"] is None
    for patch in ({"billboard": "sideways"}, {"left_rotation": [0, 0, 1]}, {"brightness": [16, 0]}):
        bad = json.loads((FIX / "entities_living.json").read_text())
        bad["entities"][9]["display"].update(patch)
        with pytest.raises(ValueError):
            validate(bad)
    bad = json.loads((FIX / "entities_living.json").read_text())
    bad["entities"][7]["attach"] = "sideways"
    with pytest.raises(ValueError):
        validate(bad)


# [operator] marks (bookmarks, spawn, worlds), containers and signs (PROTOCOL.md "Marks: bookmarks, spawn and worlds",
# "Containers", "Signs")
OPERATOR_FIXTURES = ("hello_operator", "welcome_operator", "bookmark", "bookmark_remove", "bookmarks", "error_mark_denied",
                     "region_info_worlds", "region_request_jump", "container_query", "container", "container_furnace",
                     "container_closed", "container_edit", "container_edit_put", "container_edit_sort", "container_close",
                     "error_container_denied", "sign_edit")


def test_operator_fixtures_exist_and_validate():
    for name in OPERATOR_FIXTURES:
        validate(json.loads((FIX / f"{name}.json").read_text()))


def test_operator_caps_worlds_and_marks():
    hello = json.loads((FIX / "hello_operator.json").read_text())
    welcome = json.loads((FIX / "welcome_operator.json").read_text())
    assert CAP_MARKS in hello["caps"] and CAP_MARKS in welcome["caps"]
    assert [w["env"] for w in welcome["worlds"]] == ["normal", "nether", "the_end"]
    assert welcome["marks"] == {"max": 200, "edit": True}
    assert welcome["interact"]["containers"] in ("edit", "view", "off") and welcome["interact"]["signs"] is True
    info = json.loads((FIX / "region_info_worlds.json").read_text())
    assert info["spawn"] == welcome["worlds"][0]["spawn"] and info["worlds"] == welcome["worlds"]
    jump = json.loads((FIX / "region_request_jump.json").read_text())
    nether = next(w for w in welcome["worlds"] if w["name"] == jump["world"])
    top = jump["origin"][1] + jump["dims"][1]
    assert nether["min_y"] <= jump["origin"][1] and top <= nether["max_y"], "a jump stays inside the build height"


def test_operator_builders_match_the_fixtures():
    env = Envelope()
    add = json.loads((FIX / "bookmark.json").read_text())
    built = env.wrap("bookmark", **bookmark("add", "Village", "world", (120, 64, -300)))
    validate(built)
    assert {k: built[k] for k in ("op", "name", "world", "pos")} == {k: add[k] for k in ("op", "name", "world", "pos")}
    built = env.wrap("bookmark", **bookmark("remove", "Village"))
    validate(built)
    assert "world" not in built and built["name"] == "Village"
    edit = json.loads((FIX / "container_edit.json").read_text())
    built = env.wrap("container_edit", **container_edit((1, 64, 2), 5, "minecraft:torch", 32, was=("minecraft:torch", 64)))
    validate(built)
    assert {k: built[k] for k in ("pos", "slot", "item", "count", "was")} == {k: edit[k] for k in ("pos", "slot", "item", "count", "was")}
    sort = env.wrap("container_edit", **container_edit((1, 64, 2), op="sort"))
    validate(sort)
    assert sort["op"] == "sort" and "slot" not in sort
    chest = json.loads((FIX / "container.json").read_text())
    assert container_slots(chest) == {0: ("minecraft:diamond", 3), 5: ("minecraft:torch", 64), 30: ("minecraft:oak_planks", 16)}
    assert chest["size"] == 54 and chest["other"] == [2, 64, 2]
    assert json.loads((FIX / "error_container_denied.json").read_text())["op"] == "container_edit"


@pytest.mark.parametrize("name,patch", [
    ("bookmark", {"op": "rename"}),
    ("bookmark", {"name": " "}),
    ("bookmark", {"pos": [1, 2.5, 3]}),
    ("bookmarks", {"max": 1}),
    ("bookmarks", {"marks": [{"name": "A", "world": "world", "pos": [1, 2, 3]}, {"name": "a", "world": "world", "pos": [1, 2, 3]}]}),
    ("welcome_operator", {"worlds": [{"name": "world", "env": "overworld", "min_y": 0, "max_y": 256, "spawn": [0, 64, 0]}]}),
    ("welcome_operator", {"worlds": [{"name": "world", "env": "normal", "min_y": 0, "max_y": 0, "spawn": [0, 64, 0]}]}),
    ("region_info_worlds", {"spawn": [0.5, 64, 0]}),
    ("container", {"items": [[54, "minecraft:dirt", 1]]}),
    ("container", {"items": [[0, "minecraft:dirt", 1], [0, "minecraft:stone", 2]]}),
    ("container", {"items": [[0, "dirt", 1]]}),
    ("container", {"edit": "maybe"}),
    ("container_closed", {"reason": None}),
    ("container_edit", {"slot": -1}),
    ("container_edit", {"count": 128}),
    ("container_edit", {"was": ["minecraft:torch"]}),
    ("container_edit_sort", {"op": "shuffle"}),
    ("sign_edit", {"side": "top"}),
    ("sign_edit", {"lines": ["a", "b", "c"]}),
    ("sign_edit", {"color": "teal"}),
])
def test_operator_validation_rejects(name, patch):
    msg = json.loads((FIX / f"{name}.json").read_text())
    msg.update(patch)
    with pytest.raises(ValueError):
        validate(msg)


def test_operator_error_codes():
    validate({"v": 1, "type": "error", "seq": 0, "t": 0, "code": "mark_denied", "message": "x"})


def test_sound_fixtures_and_rules():
    """[sound] sound_groups, sound actions, hello/welcome caps (PROTOCOL.md "Sound")."""
    from tabletop_protocol import CAP_ASSETS, CAP_SOUND
    g = json.loads((FIX / "sound_groups.json").read_text())
    validate(g)
    assert g["count"] == sum(len(x["blocks"]) for x in g["groups"]) == 7
    anvil = next(x for x in g["groups"] if "minecraft:anvil" in x["blocks"])
    assert anvil["volume"] == 0.3 and anvil["break"] == "block.anvil.break"
    bad = json.loads(json.dumps(g))
    bad["count"] = 8
    with pytest.raises(ValueError):
        validate(bad)
    a = json.loads((FIX / "actions_sound.json").read_text())
    validate(a)
    kinds = [x["kind"] for x in a["actions"]]
    assert kinds.count("sound") == 4 and "sound_stop" in kinds
    disc = next(x for x in a["actions"] if x.get("sound") == "music_disc.cat")
    assert disc["stream"] and disc["volume"] == 4.0
    for patch in ({"pitch": 3.0}, {"pos": [1, 2]}, {"sound": None}):
        b = json.loads(json.dumps(a))
        b["actions"][0].update(patch)
        with pytest.raises(ValueError):
            validate(b)
    assert CAP_SOUND in json.loads((FIX / "hello_sound.json").read_text())["caps"]
    assert {CAP_SOUND, CAP_ASSETS} <= set(json.loads((FIX / "welcome_sound.json").read_text())["caps"])


def test_asset_messages_and_frame():
    """[sound] assets, asset_get, asset_denied and the binary chunk frame (PROTOCOL.md "Asset delivery")."""
    from tabletop_protocol import ASSET_NAMES, asset_frame, parse_asset_frame
    offer = json.loads((FIX / "assets.json").read_text())
    validate(offer)
    assert all(f["name"] in ASSET_NAMES and len(f["sha256"]) == 64 for f in offer["files"])
    get = json.loads((FIX / "asset_get.json").read_text())
    validate(get)
    for patch in ({"name": "evil.sh"}, {"length": 2 << 20}, {"offset": -1}):
        with pytest.raises(ValueError):
            validate(dict(get, **patch))
    err = json.loads((FIX / "error_asset_denied.json").read_text())
    validate(err)
    assert err["op"] == "asset_get" and err["name"] == "sounds.zip"
    raw = (FIX / "asset_chunk.bin").read_bytes()
    assert parse_asset_frame(raw) == ("sounds.zip", 262144, b"tabletop asset chunk")
    assert asset_frame("sounds.zip", 262144, b"tabletop asset chunk") == raw
    with pytest.raises(ValueError):
        parse_asset_frame(b"XXXX" + raw[4:])


def test_assets_offer_state_and_note():
    """[setup] next version: the offer says what the automatic game files are doing (PROTOCOL.md "Asset delivery")."""
    offer = json.loads((FIX / "assets_building.json").read_text())
    validate(offer)
    assert offer["files"] == [] and offer["state"] == "building" and "26.3" in offer["note"]
    for patch in ({"state": "cooking"}, {"note": 3}):
        with pytest.raises(ValueError):
            validate(dict(offer, **patch))


def test_motion_messages():
    """[motion] lid, bell and explode actions; fuse, swell and the dying/sitting/sleeping poses (PROTOCOL.md "Block animations")."""
    a = json.loads((FIX / "actions_motion.json").read_text())
    validate(a)
    kinds = [x["kind"] for x in a["actions"]]
    assert kinds == ["lid", "lid", "bell", "explode"]
    assert [x["open"] for x in a["actions"][:2]] == [True, False]
    for i, patch in ((0, {"open": "yes"}), (0, {"pos": [1.5, 2, 3]}), (2, {"dir": "up"}), (3, {"power": 0}), (3, {"pos": [1, 2]})):
        b = json.loads(json.dumps(a))
        b["actions"][i].update(patch)
        with pytest.raises(ValueError):
            validate(b)
    e = json.loads((FIX / "entities_motion.json").read_text())
    validate(e)
    by = {x["id"]: x for x in e["entities"]}
    assert by[401]["fuse"] == 37 and by[402]["swell"] == 0.5
    assert [by[i]["pose"] for i in (403, 404, 405)] == ["dying", "sitting", "sleeping"]
    for patch in ({"fuse": -1}, {"fuse": 2.5}, {"swell": 1.5}):
        b = json.loads(json.dumps(e))
        b["entities"][0].update(patch)
        with pytest.raises(ValueError):
            validate(b)
