"""Regenerate conformance vectors with the pinned official SAM parser; not a runtime dependency."""
import base64
import importlib.metadata
import json
import random
from pathlib import Path

from meta_sam_parser import SegmentationMask, decode_mask_to_raster
from meta_sam_parser._mask_codec import _encode_raster

if importlib.metadata.version("meta-sam-parser") != "0.1.0":
    raise RuntimeError("Fixture generation requires meta-sam-parser==0.1.0")

rng = random.Random(20261030)
cases = []
for width, height in [(1, 1), (2, 3), (9, 7), (32, 24), (64, 64), (127, 33)]:
    for pattern in ["zero", "one", "random"]:
        raster = bytes(
            0 if pattern == "zero" else 1 if pattern == "one" else rng.randrange(2)
            for _ in range(width * height)
        )
        payload = _encode_raster(raster, width, height)
        decoded = decode_mask_to_raster(
            SegmentationMask(encoding="one_bit", payload=payload, width=width, height=height)
        )
        if decoded != raster:
            raise RuntimeError("Official SAM codec round trip changed fixture pixels")
        cases.append(dict(name=f"{width}x{height}-{pattern}", width=width, height=height,
                          payload=payload, rasterBase64=base64.b64encode(raster).decode("ascii")))

destination = Path(__file__).resolve().parents[1] / "tests" / "Fixtures" / "sam-one-bit.json"
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_text(json.dumps(cases, indent=2) + "\n", encoding="utf-8")
print(f"Generated {len(cases)} verified SAM masks")
