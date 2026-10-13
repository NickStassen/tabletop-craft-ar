"""Protocol error paths against a running plugin: wrong path, version mismatch, oversize subscribe, unknown type, hello timeout."""
import asyncio
import json
import os

import websockets

BASE = os.environ.get("TABLETOP_BASE", "ws://127.0.0.1:8765")
HELLO = {"v": 1, "type": "hello", "seq": 0, "t": 0, "client": "err", "protocols": [1]}


async def first(url: str, msgs: list, n: int = 3, timeout: float = 3) -> list:
    out = []
    try:
        async with websockets.connect(url) as ws:
            for m in msgs:
                await ws.send(json.dumps(m))
            for _ in range(n):
                r = json.loads(await asyncio.wait_for(ws.recv(), timeout))
                out.append((r["type"], r.get("code"), r.get("dims")))
    except websockets.ConnectionClosed as e:
        out.append(("closed", e.rcvd.code if e.rcvd else None, e.rcvd.reason if e.rcvd else None))
    except TimeoutError:
        out.append(("timeout",))
    return out


async def main() -> None:
    u = BASE + "/tabletop"
    sub = {"v": 1, "type": "subscribe", "seq": 1, "t": 0, "origin": [0, -64, 0]}
    print("wrong path      ", await first(BASE + "/", [HELLO]))
    print("version mismatch", await first(u, [dict(HELLO, protocols=[2])]))
    print("no hello first  ", await first(u, [{"v": 1, "type": "pose", "seq": 0, "t": 0, "head": {"p": [0, 0, 0]}}], 1))
    print("subscribe 128   ", await first(u, [HELLO, dict(sub, dims=[128, 64, 128])]))
    print("subscribe ok    ", await first(u, [HELLO, dict(sub, origin=[100, -64, 100], dims=[16, 8, 16])]))
    print("unknown type    ", await first(u, [HELLO, {"v": 1, "type": "dance", "seq": 1, "t": 0}]))
    print("hello timeout   ", await first(u, [], 1, timeout=10))


if __name__ == "__main__":
    asyncio.run(main())
