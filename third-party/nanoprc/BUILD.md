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
Flag bit 0 reports geometry resources with unresolved occurrence placement;
resolved PRC assemblies have this flag clear. The viewer validates output and
normalizes its bounds before uploading geometry to its separately implemented
D3D11 renderer.

Upstream notices: LICENSE and THIRD_PARTY_NOTICES.md.

PaperDesk modifications dated 2026-10-13:

- source/src/prc_api.c: assign global geometry references to first occurrences,
  scale entire rotated Cartesian basis columns, resolve representation-item
  coordinate systems and their parents, and inherit the first specified
  placement along a prototype chain. An instance's own placement, including
  explicit identity, overrides its prototype; actual parent/child placements
  are still composed. This follows the GetLocation rule in the
  [HOOPS ProductOccurrence documentation](https://docs.techsoft3d.com/publish/2023_SP2_U1/api/structA3DAsmProductOccurrenceData.html).
- source/include/prc_api.h: declare prc_api_part_local_transform.
- paperdesk-worker.cpp: traverse assembly occurrences and nested representation
  items, resolve file-scoped geometry through part_detail_index, and apply world
  transforms before calculating triangle normals. Shared resources produce
  separate placed instances. Unsupported projective/nonfinite matrices fail
  explicitly; depth, occurrence and vertex counts remain bounded.
- paperdesk-prc-tests.c: generated-fixture regression checks for separate file
  structures, shared geometry, coordinate chains, rotated nonuniform scale and
  prototype-placement inheritance (including explicit identity), plus the
  standalone worker's actual geometry, normals and parent/child composition.

Regression checks, from the distribution's third-party directory (the fixture output path must be
writable and contain only ASCII characters because nanoPRC's file API is narrow):

```
cmake --build build-model-worker --config Release --target PaperDeskPrcTests
build-model-worker/Release/PaperDeskPrcTests.exe build-model-worker/regression.prc build-model-worker/Release/PaperDeskModelWorker.exe
```

In the full PaperDesk repository, tools/Build-3D.ps1 -Test builds and runs these
checks. Neither that test nor the worker modifies its input PDF/model files.
