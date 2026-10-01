# Brimstone Core for Xbox - architecture

## Product role

The Xbox build is a supported Brimstone renderer and 10-foot Control surface.

Core remains authoritative for authentication, device trust, library/catalogue
data, provider credentials, metadata, artwork, ripping/ingest and signed
playback stream URLs.

Xbox owns the controller-first TV UI, Xbox audio playback, background media
playback, the local endpoint control listener and supported optical capability
probing.

## Trust and pairing

Xbox uses the existing Core device-trust contract:

1. Sign into Core.
2. Request a one-time pairing code for role `core-end`.
3. Exchange it using the stable Xbox device ID.
4. Store the returned per-device endpoint credential.
5. Register at `/api/v1/endpoints/register`.
6. Core uses that credential for Xbox endpoint control.

The endpoint ID comes from `SystemIdentification.GetSystemIdForPublisher()`
so rebooting the console does not create another Brimstone output.

## Endpoint protocol

The Xbox listener mirrors the normal Core End v1 surface:

- `GET /v1/capabilities`
- `GET /v1/status`
- `POST /v1/prepare`
- `POST /v1/start`
- `POST /v1/play`
- `POST /v1/programme`
- `POST /v1/pause`
- `POST /v1/resume`
- `POST /v1/seek`
- `POST /v1/stop`

Development listener port: `8095/tcp`.

## Background audio

Playback uses `Windows.Media.Playback.MediaPlayer` and the package declares
`backgroundMediaPlayback`. The build is audio-first and deliberately does not
request Xbox 4K-video capabilities.

## Controller model

Normal XAML XY focus handles D-pad/left-stick navigation and A/Select.

Brimstone adds:

- View -> Now Playing
- Menu -> Settings
- B -> back from album detail

## Optical strategy

The package declares Microsoft's documented `optical` device capability, but
Beta 1 does not assume the Xbox One X internal UHD drive exposes raw audio data.

Production rule:

- supported Microsoft APIs only;
- feature-detect on real Xbox hardware;
- no undocumented interfaces, exploits or console modification;
- if useful local optical access is unavailable, a drive attached to normal
  Brimstone Core performs the rip and Xbox/iPad Control operates the job.

## Audio output

Audio is rendered through the Xbox system audio stack. HDMI/S/PDIF selection and
format negotiation remain Xbox system settings. We do not call the Xbox path
bit-perfect until it has been measured on real hardware.
