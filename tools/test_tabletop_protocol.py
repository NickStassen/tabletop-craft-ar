import json
import math
import pathlib

import pytest

from tabletop_protocol import Envelope, WorldMirror, decode_rle, scripted_pose, validate

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
