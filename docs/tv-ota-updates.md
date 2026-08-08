# Native TV OTA architecture

NubArca TV uses `expo-updates` protocol v1. The API serves anonymous Android
manifests at `/api/tv-app/updates` and immutable, hash-addressed assets below
that path. Publications and channel pointers are keyed by the tracked runtime,
so incompatible clients receive `204 No Content`.

The installed APK renders its embedded/cached bundle immediately, performs one
background check, downloads a compatible signed update and selects it only on a
later cold launch. It never calls `reloadAsync`. The API validates the exact
manifest signature and all referenced asset hashes before serving them; the
client independently validates against the certificate embedded in its APK.

OTA can change only JavaScript and Metro-bundled assets compatible with the
existing native contract. Native/configuration changes require a new APK and
runtime. Runtime identity, signing, publication, validation, rollback-pointer,
cleanup and device acceptance are governed exclusively by the canonical
[TV release runbook](tv-release.md).
