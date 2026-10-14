import json
import math
import pathlib

import pytest

from tabletop_protocol import (
    CAP_BIOMES,
    CAP_EDIT,
    CAP_LIGHT,
    Envelope,
    WorldMirror,
    block_edit,
    decode_light,
    decode_rle,
    parse_region,
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
