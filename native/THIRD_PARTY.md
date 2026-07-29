# Native third-party sources

- `vendor/miniaudio` is miniaudio 0.11.25, imported from
  <https://github.com/mackron/miniaudio/releases/tag/0.11.25>. It is available
  under the public-domain or MIT-0 terms in its `LICENSE`.
- `vendor/libfvad` is libfvad v1.0, imported from
  <https://github.com/dpirch/libfvad/releases/tag/v1.0>. It is the standalone
  WebRTC VAD implementation; retain its `LICENSE`, `PATENTS`, and `AUTHORS`.

`FoxTrans.Native` compiles both sources into one RID-specific native library.
