#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/Samples/MobileSmoke/MobileSmoke.csproj"
CONFIGURATION="${CONFIGURATION:-Release}"
RUNTIME="${RUNTIME:-iossimulator-arm64}"
BUNDLE_ID="com.newlife.smoke.novadb"
TIMEOUT="${TIMEOUT:-180}"
ARTIFACTS="$ROOT/Bin/MobileSmoke/artifacts"

mkdir -p "$ARTIFACTS"

# 用 perl 的 alarm 实现超时（macOS 自带 perl，无 GNU timeout）
run_limited() {
  local secs="$1"; shift
  perl -e 'alarm shift; exec @ARGV' "$secs" "$@"
}

echo "[ios] xcodebuild: $(xcodebuild -version | head -1)"
xcrun simctl list runtimes | sed -n '1,20p'
xcrun simctl list devices available | sed -n '1,30p'

echo "[ios] build start: $(date -u +%H:%M:%S)"
run_limited 1200 dotnet build "$PROJECT" -c "$CONFIGURATION" -f net10.0-ios -p:MobileTargetFramework=net10.0-ios -p:RuntimeIdentifier="$RUNTIME" --nologo
echo "[ios] build done: $(date -u +%H:%M:%S)"

APP="$(find "$ROOT/Bin/MobileSmoke" -maxdepth 6 -name "*.app" -print -quit)"
if [ -z "$APP" ]; then
  echo "iOS app bundle not found."
  exit 1
fi

UDID="$(xcrun simctl list devices available | awk -F '[()]' '/iPhone/ {print $2; exit}')"
if [ -z "$UDID" ]; then
  echo "No iPhone simulator available."
  exit 1
fi

echo "[ios] booting simulator $UDID: $(date -u +%H:%M:%S)"
xcrun simctl boot "$UDID" 2>/dev/null || true
run_limited 300 xcrun simctl bootstatus "$UDID" -b
echo "[ios] simulator booted: $(date -u +%H:%M:%S)"
xcrun simctl install "$UDID" "$APP"
xcrun simctl terminate "$UDID" "$BUNDLE_ID" 2>/dev/null || true
xcrun simctl launch "$UDID" "$BUNDLE_ID"

RESULT=""
DEADLINE=$((SECONDS + TIMEOUT))
while [ "$SECONDS" -lt "$DEADLINE" ]; do
  DATA_DIR="$(xcrun simctl get_app_container "$UDID" "$BUNDLE_ID" data 2>/dev/null || true)"
  if [ -n "$DATA_DIR" ]; then
    RESULT="$(find "$DATA_DIR" -name smoke.json -print -quit 2>/dev/null || true)"
    if [ -n "$RESULT" ]; then break; fi
  fi
  sleep 1
done

if [ -z "$RESULT" ]; then
  echo "smoke.json not found within ${TIMEOUT}s."
  xcrun simctl spawn "$UDID" log show --last 2m --style compact --predicate 'process == "MobileSmoke"' || true
  exit 1
fi

cp "$RESULT" "$ARTIFACTS/ios-smoke.json"
cat "$RESULT"
grep -q '"fail":0' "$RESULT"
echo "iOS smoke passed."
