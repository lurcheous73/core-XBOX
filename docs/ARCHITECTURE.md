# Xbox Control R1 architecture

Core owns authentication, catalogue, personal subscriptions, queues, providers,
stream tickets and optional compatible video conversion. Xbox owns its native
controller UI, MediaPlayer rendering and authenticated Core-End listener.

There is no Xbox-hosted Core API, local database, scanner, ripper or supervisor.
Only the Core-End renderer listener remains, on its existing authenticated port
8095. It requires the paired device credential and is distinct from Core's API.

The package keeps its existing identity and app-data location. Legacy bearer
credentials migrate to PasswordVault; reconnecting/signing out invalidates the
visible connection context. Late catalogue responses cannot replace another
Core's screen. Transport writes are issued once, without retry fallback.

Music uses the published R1 collections with a consistent publication revision,
opaque item IDs and exact variants. Native Sooloos queue writes omit NAS variants
so Core can resolve the selected broker's native copy and require confirmation.
Radio uses favourites → recent → popular and registers the chosen stream with
Core before requesting playback. A failed favourites request is not treated as
an empty list. Video tickets must be relative, bind the exact recording and
contain a temporary token; account bearer tokens are never placed in player URLs.

The iOS-derived visual system uses a 1600×900 TV design canvas, safe margins,
large cover tiles, controller focus and separate Playing/queue columns. Display
scaling is separate from verification of a decoder's actual output resolution.
