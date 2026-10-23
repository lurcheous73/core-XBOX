# Brimstone Xbox Control — R1 revision 29

Xbox is a Control client and streaming renderer for an existing Brimstone Core.
This candidate replaces the former standalone Xbox Core. It uses the same purple
Brimstone identity, cover tiles and Home / Playing / Your Music / Streaming /
Spoken Word / Videos / Settings structure as the current iOS Control.

## R1 behaviour

- Published Core collections retain exact catalogue item and variant identities.
  Bootlegs are a Your Music submenu and never populate Home.
- Playing includes transport, speaker selection, seek, volume and Clear queue.
  Play and Add to queue are separate operations; append never replays an album.
- Radio follows Core favourites, recent, then popular in Core order, with metadata.
- Videos have band filters, artwork and recording metadata. Select a tile to play
  a temporary Core stream. Compatible delivery can be supplied by Core; no movie
  download or local media cache is created by Control.
- Personal podcasts and audiobooks stay on the authenticated Core account.
- The Xbox player retains background audio and authenticated Core-End pairing.
  Audio routing follows Xbox system settings, including HDMI/optical where
  supported. The tvOS-only HDMI restriction is not copied to other clients.
- Normal XAML focus supports controllers. View opens Playing, Menu opens Settings,
  and B goes back. The safe-area 16:9 canvas scales for 1080p and 4K displays.

## Xbox Core retired

The native Core runtime, supervisor, local catalogue/database, local Core API,
optical probing, CD/Blu-ray ingestion and their dependencies have been removed
from the application. No local administrator is generated and no Core is started.
The existing package identity and LocalFolder are retained so this change does
not erase an installed application's data. Previous server code is available in
Git history; its old runtime notes are under `docs/archive/`.

User and endpoint credentials migrate from legacy settings into Windows
PasswordVault. Public remote connections require HTTPS; HTTP remains available
for explicit local-network Core addresses. No certificate-validation bypass is
installed. An existing Core and valid account are required.

## Builds and acceptance

Development package version: **2.5.0.0**, x64 UWP, minimum Windows/Xbox API 16299.
The repository workflows compile XAML/C# and create Dev Mode packages on Windows.
Dev packages are separately signed for development and are not a retail Store
release. The existing Partner Center package identity is retained in Store
packaging; it is not a second Core runtime.

Native compilation, package verification and physical console acceptance must
be recorded separately. Do not claim measured audio, 4K decoding or Xbox
controller acceptance from source inspection or desktop tests alone.
