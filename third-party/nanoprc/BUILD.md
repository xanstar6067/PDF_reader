# PaperDesk model worker source

This directory contains the corresponding source for the independently executable
PaperDeskModelWorker, licensed under GNU AGPL version 3. The native PRC reader is
nanoPRC from https://github.com/mvrhel/nanoPRC, downloaded 2026-10-12 from main.
The PaperDesk-specific worker implements the documented PD3D binary interchange
format and an initial uncompressed U3D reader. It does not link into PaperDesk's
WPF or D3D11 implementation. All decoder changes must be distributed here.

Dependencies: CMake 3.20+, Visual Studio C++ Build Tools, Windows SDK, and the
included ../zlib/source (zlib 1.3.1, its separate license is ../zlib/LICENSE).
No network access is needed to compile this source.

From the distribution's third-party directory:

```
cmake -S nanoprc -B build-model-worker -A x64
cmake --build build-model-worker --config Release
```

Usage: PaperDeskModelWorker model.bin model.pd3d. Input is a raw U3D or PRC stream;
the program neither uploads models nor executes PDF/3D JavaScript. Output is
PD3D0001 followed by little-endian uint32 flags and part count. Each part has a
uint32 UTF-8 name byte count, name bytes, uint32 vertex count and 40-byte vertices
(float position[3], normal[3], color[4]). Vertices are independent triangle lists.
Flag bit 0 reports approximate/local placement. The viewer validates output and
normalizes its bounds before uploading geometry to its separately implemented
D3D11 renderer.

Upstream notices: LICENSE and THIRD_PARTY_NOTICES.md. No upstream source has been
changed, except files explicitly documented below when necessary.
