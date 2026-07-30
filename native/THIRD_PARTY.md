# Native third-party sources

- `vendor/miniaudio` is the minimal source subset of miniaudio 0.11.25 from
  <https://github.com/mackron/miniaudio/releases/tag/0.11.25>. It is available
  under the public-domain or MIT-0 terms in its retained `LICENSE`.
- `vendor/libfvad` is the minimal source subset of libfvad v1.0 from
  <https://github.com/dpirch/libfvad/releases/tag/v1.0>. It is the standalone
  WebRTC VAD implementation; retain its `LICENSE`, `PATENTS`, and `AUTHORS`.
- `vendor/openvr` is the minimal source subset of Valve OpenVR from
  <https://github.com/ValveSoftware/openvr>, commit
  `0924064316de3effbcd1acf1e309182a2deb1c05`. It is available under the
  BSD 3-Clause terms in its retained `LICENSE`.

`FoxTrans.Native` compiles all curated source sets into one RID-specific native
library.
