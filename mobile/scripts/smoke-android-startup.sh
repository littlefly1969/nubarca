#!/usr/bin/env bash
set -euo pipefail

apk="${1:?usage: smoke-android-startup.sh APK [PACKAGE] [EXPECTED_TEXT]}"
package_name="${2:-it.littlefly.nubarca}"
expected_text="${3:-NubArca}"
activity="$package_name/.MainActivity"

dump_failure_log() {
  adb logcat -d -v threadtime \
    AndroidRuntime:E ReactNativeJS:V ReactNative:V ExpoModulesCore:V SoLoader:V libc:F '*:S' \
    || true
}

adb install -r "$apk"
adb shell pm clear "$package_name" >/dev/null
adb shell am force-stop "$package_name"
adb logcat -c
adb shell am start -W -n "$activity"

# A live process is insufficient: a debuggable build can stay resumed while
# React Native shows its red "Unable to load script" surface. Wait until the
# expected first-screen text is present in Android's accessibility tree, which
# proves that native registration, JavaScript loading and the first React
# render all completed.
ui_dump=""
ui_ready=false
deadline=$((SECONDS + 45))
while (( SECONDS < deadline )); do
  if ! adb shell pidof "$package_name" >/dev/null; then
    break
  fi
  adb shell uiautomator dump /sdcard/nubarca-startup.xml >/dev/null 2>&1 || true
  ui_dump="$(adb shell cat /sdcard/nubarca-startup.xml 2>/dev/null | tr -d '\r' || true)"
  if grep -Fq "text=\"$expected_text\"" <<<"$ui_dump" ||
    grep -Fq "content-desc=\"$expected_text\"" <<<"$ui_dump"; then
    ui_ready=true
    break
  fi
  sleep 2
done

pid="$(adb shell pidof "$package_name" | tr -d '\r')"
if [[ -z "$pid" ]]; then
  echo "Mobile startup smoke failed: $package_name exited after launch." >&2
  dump_failure_log >&2
  exit 1
fi

if [[ "$ui_ready" != true ]]; then
  echo "Mobile startup smoke failed: expected UI text not rendered: $expected_text" >&2
  printf '%s\n' "$ui_dump" >&2
  dump_failure_log >&2
  exit 1
fi

activities="$(adb shell dumpsys activity activities)"
if ! grep -Eq "topResumedActivity=.*${package_name//./\\.}/\.MainActivity" <<<"$activities"; then
  echo "Mobile startup smoke failed: MainActivity is not the resumed activity." >&2
  printf '%s\n' "$activities" | grep -F "$package_name" >&2 || true
  dump_failure_log >&2
  exit 1
fi

echo "Mobile startup smoke passed: package=$package_name pid=$pid activity=.MainActivity text=$expected_text"
