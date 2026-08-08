# NubArca TV APK distribution architecture

NubArca TV is a separate Android application with package
`it.littlefly.nubarca.tv`. Android permits an in-place update only when the
applicationId is unchanged, `versionCode` increases and the APK retains the
definitive Android signing certificate.

The native APK also embeds the public OTA certificate. That certificate is
independent from the Android JKS: Android verifies APK replacement, while
`expo-updates` verifies signed JavaScript/asset manifests. The APK contains
neither private OTA key nor Android keystore credentials.

The public routes `${NUBARCA_PUBLIC_ORIGIN}/tv.apk` and
`${NUBARCA_PUBLIC_ORIGIN}/download/tv/nubarca-tv.apk` serve the same canonical
artifact; the latter also has a `.sha256` sidecar. All build, local validation,
physical-device acceptance and atomic publication commands live only in the
canonical [TV release runbook](tv-release.md).
