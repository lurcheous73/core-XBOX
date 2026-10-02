# Xbox Native Core Runtime

Status: implementation scaffold, Beta 1 experimental.

## Goal

Reuse Xbox One / Xbox One X / Series hardware as a self-contained Brimstone music appliance.

The Xbox package is the appliance boundary. The implementation preserves Core service boundaries and API contracts, but the Xbox package does not require Docker or WSL. Components are compiled into the package as Xbox-compatible libraries/background components and use the app's isolated storage.

## Runtime layout

- Authority: native Xbox Core authority process.
- API: private-LAN Core-compatible HTTP surface on port 8096.
- Storage: ApplicationData LocalFolder under Core/.
- Catalogue: SQLite-backed logical catalogue (next implementation slice).
- Queue/session: native playback queue/session state.
- Renderer: Windows MediaPlayer with background-media capability.
- Sooloos: direct broker already compiled.
- Ingest: Xbox optical capability probe, then auto-rip if the platform exposes supported access.
- Providers: migrate only provider adapters that fit Xbox resource/background constraints.

## Storage tree

Core/
- Catalogue/
- Media/
- Cache/
- Logs/
- Ingest/

## Current native API

- GET /api/v1/health
- GET /api/v1/runtime

These endpoints intentionally expose implementation truth while the native catalogue/provider/ingest slices are migrated.

## Product UX

The TV shell remains couch-first: Your Music, Queue, Now Playing and Rip Disc. Administration remains primarily in Control/phone/tablet. The Xbox should behave like an appliance, not a server dashboard.

## Migration plan

1. Native authority/storage/API scaffold.
2. SQLite logical catalogue and local-library scan.
3. Queue/history/resume persistence.
4. Native provider subset and Sooloos state.
5. Optical/UHD supported-API probe and auto-rip pipeline.
6. External storage and backup/import within Xbox brokered-access limits.
7. Remove transitional dependency on an external Core for ordinary local playback/library functions.
