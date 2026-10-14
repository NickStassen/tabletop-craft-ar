import json
import math
import pathlib

import pytest

from tabletop_protocol import Envelope, WorldMirror, decode_rle, parse_region, scripted_pose, validate

FIX = pathlib.Path(__file__).parent / "fixtures"


def snap():
    return {"v": 1, "type": "chunk_snapshot", "seq": 0, "t": 0, "snapshot_id": 1, "world": "world",
            "origin": [10, 60, 20], "dims": [2, 2, 2], "order": "yzx", "palette": ["minecraft:air", "minecraft:stone"],
            "encoding": "rle_palette_v1", "data": [1, 1, 0, 7], "biomes": None, "light": None}


@pytest.mark.parametrize("path", sorted(FIX.glob("*.json")), ids=lambda p: p.stem)
def test_spec_fixtures_validate(path):
    validate(json.loads(path.read_text()))


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
