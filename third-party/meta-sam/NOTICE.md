# Meta SAM codec attribution and conformance data

`src/OmniTwin.Perception/Vision/SamOneBitMaskCodec.cs` adapts the one_bit codec from `meta-sam-parser` 0.1.0 by Meta Platforms, Inc. and affiliates. The adaptation and generated conformance data are distributed under the accompanying [SAM License](LICENSE). Copyright (c) Meta Platforms, Inc. and affiliates. All Rights Reserved.

Sources:

- [Official SAM client-library documentation](https://dev.meta.ai/docs/sam/client-libraries)
- [PyPI package version 0.1.0](https://pypi.org/project/meta-sam-parser/0.1.0/)
- [Meta source repository](https://github.com/meta-models/meta-sam)

The inspected wheel was `meta_sam_parser-0.1.0-py3-none-any.whl`, SHA-256 `eca156907911c56cef3a350ffc81c34b7e70655e5fa3ccdddb77042c541a0152`. Its hash was checked before inspection.

`tests/Fixtures/sam-one-bit.json` contains eighteen binary-mask vectors generated and verified by that package: widths/heights 1x1, 2x3, 9x7, 32x24, 64x64, and 127x33; each has zero, one, and deterministic random patterns. The Python random seed is 20261030. The C# decoder is checked byte-for-byte against those rasters in both core targets. The production .NET code has no Python dependency.

To regenerate fixtures in a development Python environment:

```powershell
python -m pip install meta-sam-parser==0.1.0
python tools/generate_sam_fixtures.py
```
