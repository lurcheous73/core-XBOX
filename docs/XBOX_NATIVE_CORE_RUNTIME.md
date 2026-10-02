# Xbox Native Core Runtime

Status: Beta 1 experimental. Native Core stack milestone: 0.2.0.

## Goal

Reuse Xbox One / Xbox One X / Series hardware as a self-contained Brimstone music appliance.

The Xbox AppContainer is the appliance boundary. Xbox does not expose the host/container
facilities required to run the normal Linux Docker daemon, so the Xbox build preserves the
Core Compose service graph and API contracts while implementing each service as an
Xbox-compatible module supervised inside the Brimstone package.

Core edition must not require a second Brimstone Core to boot or play Xbox-local media.
An external Core may be attached only as an optional migration/library bridge.

## Compose-compatible stack

The Xbox supervisor keeps the canonical Docker Compose service identities:

| Compose service | Xbox implementation |
| --- | --- |
| core-postgres | Embedded persistent Core catalogue authority under LocalState/Core/Catalogue |
| core-cast | Native MediaPlayer renderer and cast/Sooloos broker |
| surroundcore | Native Xbox Core authority |
| surroundcore-web | StreamSocket Core HTTP/API surface |
| surround-ingest | CustomDevice optical ingest, raw CDDA reader and disc watcher |

The supervisor preserves dependency order, health, restart-policy metadata and persisted
service state. Its equivalent of `docker compose ps` is stored at:

`LocalState/Core/Stack/compose-state.json`

and exposed through:

- GET /api/v1/stack
- GET /api/v1/system/stack

## Runtime layout

- Authority: native Xbox Core runtime.
- API: Core-compatible HTTP surface, currently port 8096.
- Storage: ApplicationData LocalFolder under Core/.
- Catalogue: embedded persistent compatibility store plus native media scan.
- Queue/session: native MediaPlayer queue.
- Renderer: Windows MediaPlayer with background-media capability.
- Sooloos: direct Meridian/Sooloos broker.
- Ingest: native CustomDevice optical service with periodic insert detection.
- Providers: migrate provider adapters individually where compatible with Xbox constraints.

## Storage tree

Core/
- Catalogue/
  - core-database.json
- Media/
- Cache/
- Logs/
- Ingest/
  - auto-rip.json
  - CD-*/
    - rip-status.json
    - Track NN.wav
- Stack/
  - compose-state.json

## Native Core behaviour implemented

- Core/Sooloos first-run personality.
- Core mode boots the local Xbox Core stack without external login.
- Xbox-local ripped albums appear in Your Music.
- Local tracks/programmes play directly from AppContainer storage.
- Native queue and transport are used in Core mode.
- External Core connection remains optional for migration/testing.
- Optical service watches for newly inserted audio CDs every 8 seconds.
- Completed rips persist per-track SHA-256 data and eject the disc.

## Optical validation

Series X internal drive observed as PLDS DG-6M5S SCSI CdRom Device.

Validated from inside the Brimstone AppContainer:

- CD-ROM device interface enumeration allowed.
- CustomDevice raw handle opens.
- IOCTL_CDROM_READ_TOC succeeds.
- IOCTL_CDROM_RAW_READ returns 2352-byte CDDA sectors.
- Full ten-track audio CD rip completed.
- 176,295 audio sectors written and hashed.
- Measured full-disc rip speed: 5.19x realtime in the validation run.
- IOCTL_STORAGE_EJECT_MEDIA succeeds after completion.
- MusicBrainz identification from the measured TOC was also validated externally during development.

## Current native API

- GET /api/v1/health
- GET /api/v1/runtime
- GET /api/v1/stack
- GET /api/v1/system/stack
- GET /api/v1/optical/probe

## Product UX

The TV shell remains couch-first: Your Music, Queue, Now Playing and Rip Disc.
Administration belongs primarily in Control/phone/tablet. The Xbox should behave like
an appliance, not a server dashboard.

## Next migration slices

1. Metadata lookup and artwork publication inside the Xbox auto-rip flow.
2. Lossless FLAC output/storage policy after raw-read verification.
3. Richer embedded catalogue schema, history, resume and favourites.
4. Provider subset and credentials compatible with the Xbox runtime.
5. External USB storage and backup/import using brokered Xbox storage access.
6. LAN Control discovery/API validation and hardening.
7. Store/retail background-media validation.
