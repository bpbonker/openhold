# Design measurements

Measurements behind three design decisions: reading Inventor files on the server, checking AutoCAD Electrical
projects in and out as one set, and the chunk size used for storage and replication. Measured in October 2026
on a workstation with Inventor 2025 and AutoCAD Electrical 2025, on copies of production files. Reproduce with
`tools/Openhold.Spike`.

## Apprentice reads real Inventor data outside Inventor

`Openhold.Spike refs <file>` on a 6 MB production assembly:

- Opened in 241 ms, 313 ms total including a recursive reference walk (6 files).
- Read Part Number, Description, Mass, Volume, Surface Area, Designer, and the Inventor version that saved the file.
- No Inventor licence or running Inventor needed.

**Conclusion:** a server-side indexer can build where-used/uses and property data at check-in.

## AutoCAD Electrical project sets parse cleanly

`Openhold.Spike wdp <project.wdp>` on the shipped demo project: 9 drawings with sheet descriptions and
sub-folders, plus companion files (`.wdt`, `_wdtitle.wdl`, `.aepx`). That gives a 13-file project set,
which is checked out and in as one batch.

## Chunk dedup on real re-saved Inventor files

`Openhold.Spike dedup <older> <newer>`, comparing each file's `OldVersions` copy with its current version:

| File | Size | Reused at 16 KB avg | 64 KB | 256 KB | 1 MB |
|---|---|---|---|---|---|
| 4 chassis parts (`.ipt`) | 1.2–4.2 MB | 0% | 0% | 0% | 0% |
| Ancillaries assembly (`.iam`) | 3.5 MB | 74% | 71% | 58% | 36% |
| Platform part, unchanged re-save | 0.9 MB | 100% | 100% | 100% | 100% |

**Conclusions:**

1. Edited parts are rewritten almost completely on save, so chunk dedup does not help them. The main
   saving is file-level: only files whose content changed are transferred. A 4 MB part crosses a
   100 Mbps link in about 0.4 s.
2. Assemblies do dedup well, but only with small chunks. The default average chunk size is therefore
   **64 KB** (min 16 KB, max 256 KB), not 1 MB.
3. Small chunks would make one request per chunk round-trip-bound: about 27 Mbps at 150 ms with 8
   parallel requests. Chunks are therefore transferred in **batches of ~4 MB per request**
   (`ChunkFrames`, `/api/chunks/upload`, `/api/chunks/fetch`).

## Still to measure

- iperf3 between offices on different continents, single stream vs parallel streams.
- Dedup across a wider sample: more assemblies, drawings (`.idw`/`.dwg`), and ACADE drawings after
  project-wide updates.
- Add-in event hooks (`OnFileDirty`, `OnSaveDocument`) in Inventor 2025, 2026 and 2027.
