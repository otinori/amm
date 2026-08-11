#!/usr/bin/env bash
# macOS counterpart of tools/build-installer-tauri.cmd. Builds the .app
# bundle (and .dmg, when the environment allows it - see below) via
# cargo-tauri. See publish-tauri-macos.sh's header for the amm-mcp
# staging/PATH caveat.
#
# Steps:
#   1. tools/publish-tauri-macos.sh (stages amm-mcp, builds amm via plain
#      cargo build, sanity-checks the binary size)
#   2. cargo tauri build (produces the .app and, environment permitting,
#      a .dmg)
#   3. re-check the binary size (root-caused and fixed 2026-08-03: missing
#      `default-run = "amm"` in Cargo.toml's [package] left tauri-cli's
#      RustAppSettings::get_binaries() unable to tell amm/amm-mcp apart as
#      "the main binary" whenever a crate has 2+ bin targets with neither
#      `[[bin]]` nor `default-run` set - src/interface/rust.rs's own
#      `match binaries.len() { 0=>.., 1=>.., _=>{} }` never marks either as
#      main in that case. Kept as a safety net; shouldn't trigger anymore)
#   4. re-sign the .app as a defensive backstop for the rare case where
#      step 3 actually patched the binary (see the codesign line's own
#      comment below for why this is normally a no-op)
# Output: artifacts/target/release/bundle/macos/amm.app
#         artifacts/target/release/bundle/dmg/*.dmg (if produced)
#         copied to artifacts/packages/tauri-macos/
#
# Requires: cargo-tauri (`cargo install tauri-cli --version ^2`).
#
# KNOWN ENVIRONMENT CAVEAT (tasks/retro-pending.md, found 2026-07-29): the
# .dmg bundler's Finder-decoration AppleScript step needs macOS's
# Automation/TCC permission granted (System Settings > Privacy & Security
# > Automation) to whatever process runs this script, to control Finder.
# In a non-interactive/agent-driven session nobody can click the
# permission prompt, so bundle_dmg.sh fails with an AppleEvent timeout
# even though the .app itself builds and signs fine. This script does NOT
# treat a failed .dmg step as fatal - it still copies the .app to
# artifacts/packages/tauri-macos/ and reports the .dmg failure as a warning, since an
# interactive user session (or one with the permission already granted)
# should succeed at the .dmg step without any code changes here.
set -uo pipefail

cd "$(dirname "$0")/.."

echo "=== Step 1/3: publish-tauri-macos (stages amm-mcp, builds amm) ==="
if ! "$(dirname "$0")/publish-tauri-macos.sh"; then
  echo
  echo "*** publish-tauri-macos.sh failed ***" >&2
  exit 1
fi

# tauri.conf.json's bundle.resources harvests everything under resources/
# including the tracked .gitkeep placeholder - drop it before bundling so
# it does not ship inside the .app, then restore it so the working tree
# stays clean for git (mirrors build-installer-tauri.cmd exactly).
rm -f src/apps/Amm/src-tauri/resources/.gitkeep

echo
echo "=== Step 2/3: cargo tauri build (app + dmg) ==="
(cd src/apps/Amm/src-tauri && cargo tauri build)
TAURI_BUILD_ERR=$?

touch src/apps/Amm/src-tauri/resources/.gitkeep

if [[ $TAURI_BUILD_ERR -ne 0 ]]; then
  # cargo-tauri's own build step (before it even reaches bundling) failing
  # is fatal - only a downstream dmg-specific failure (handled below via
  # the .app existing regardless) is treated as non-fatal.
  APP_PATH="artifacts/target/release/bundle/macos/amm.app"
  if [[ ! -d "$APP_PATH" ]]; then
    echo
    echo "*** cargo tauri build failed before producing amm.app ***" >&2
    exit 1
  fi
  echo
  echo "*** cargo tauri build reported an error (exit $TAURI_BUILD_ERR), but amm.app was produced - likely the known .dmg/Automation-permission gap (see header comment). Continuing with the .app. ***" >&2
fi

APP_PATH="artifacts/target/release/bundle/macos/amm.app"
APP_BINARY="$APP_PATH/Contents/MacOS/amm"

echo
echo "=== Step 3/3: re-check binary size and ad-hoc sign ==="
amm_size=$(stat -f%z artifacts/target/release/amm 2>/dev/null || echo 0)
amm_mcp_size=$(stat -f%z artifacts/target/release/amm-mcp 2>/dev/null || echo 0)
if (( amm_size < 5000000 )) || [[ "$amm_size" == "$amm_mcp_size" ]]; then
  echo "*** sanity check (after cargo tauri build) FAILED: amm is ${amm_size} bytes (amm-mcp is ${amm_mcp_size}) - the known cargo-tauri binary-swap corruption (tasks/retro-pending.md) reproduced again. Patching Contents/MacOS/amm from the real deps/amm-<hash> binary. ***" >&2
  # Sort by mtime (newest first), not size: cargo tauri build's own
  # internal `cargo build --bins --features tauri/custom-protocol
  # --release` produces a genuinely different (and the one actually meant
  # to ship - custom-protocol is what makes the bundled app serve its
  # embedded assets instead of expecting a dev server) amm-<hash> than the
  # plain `cargo build --release` this script ran in step 1, so "biggest"
  # is not a reliable way to pick the right one - "most recently built" is.
  real_amm=$(find artifacts/target/release/deps -maxdepth 1 -type f -perm -u+x -name 'amm-*' ! -name 'amm_mcp-*' -exec ls -t {} + 2>/dev/null | head -1)
  if [[ -z "$real_amm" ]]; then
    echo "*** could not locate the real GUI binary under deps/ to patch from - aborting ***" >&2
    exit 1
  fi
  cp "$real_amm" artifacts/target/release/amm
  cp "$real_amm" "$APP_BINARY"
  chmod +x artifacts/target/release/amm "$APP_BINARY"
fi

# tauri.conf.json's bundle.macOS.signingIdentity ("-") already makes
# tauri-bundler itself ad-hoc-sign the .app correctly (inside-out: nested
# binaries first, then the bundle - see tauri-bundler's app::bundle_project
# / tauri-macos-sign's Keychain::sign) *before* the .dmg gets built in the
# same `cargo tauri build` call above, so the .dmg already ships a
# properly-signed .app in the normal case. This line only matters as a
# backstop for the rare case where the sanity check above just overwrote
# Contents/MacOS/amm with a different binary (the resulting .app needs a
# fresh signature - the codesign that ran during `cargo tauri build`
# no longer matches the patched binary) - the .dmg itself is NOT rebuilt
# in that case and would still carry the stale, now-invalid signature; if
# the sanity check above ever actually fires, the .dmg needs a manual
# rebuild too (not automated here since the check is a safety net that
# "shouldn't trigger anymore").
#
# Root cause found 2026-08-09: before bundle.macOS.signingIdentity was
# configured, tauri-bundler's app::bundle_project() only signs when a
# signingIdentity is set (`sign::keychain(None)` returns `Ok(None)`,
# skipping signing entirely) - so the .app baked into the .dmg had no
# seal on its resources at all (only the raw executable's automatic
# linker-applied ad-hoc signature), while this script's own post-hoc
# `codesign --deep` only ever touched the standalone .app copy, never the
# .dmg (already built earlier, by cargo tauri build, from the unsigned
# .app). `codesign --verify` on the .dmg's amm.app showed "code has no
# resources but signature indicates they must be present" - macOS
# Gatekeeper turns that into "'amm' is damaged and can't be opened" on
# download, which right-click-Open does NOT bypass (unlike the classic
# "unidentified developer" prompt). `xattr -d com.apple.quarantine
# amm.app` was a viable workaround, but shipping a genuinely broken
# signature instead of fixing the build was never the right call.
codesign --sign - --force --deep "$APP_PATH"

mkdir -p artifacts/packages/tauri-macos
# `cp -R` into an already-existing artifacts/packages/tauri-macos/amm.app merges files
# rather than replacing the bundle wholesale - a stale amm.app left over
# from an earlier build/session can leave mismatched files behind that
# don't match the freshly-signed _CodeSignature/CodeResources manifest,
# which macOS then refuses to launch (SIGKILL / Code Signature Invalid).
# Found via a real crash on this machine (tasks/retro-pending.md) after a
# fresh build merged into a package dir left over from an earlier round.
rm -rf artifacts/packages/tauri-macos/amm.app
cp -R "$APP_PATH" artifacts/packages/tauri-macos/
DMG_PATH=$(find artifacts/target/release/bundle/dmg -maxdepth 1 -name '*.dmg' 2>/dev/null | head -1)
if [[ -n "$DMG_PATH" ]]; then
  cp "$DMG_PATH" artifacts/packages/tauri-macos/
else
  echo
  echo "*** No .dmg was produced (see header comment re: Automation permission). amm.app is still available in artifacts/packages/tauri-macos/. ***" >&2
fi

echo
echo "*** macOS build finished ***"
echo "Output: $(pwd)/artifacts/packages/tauri-macos/"
ls artifacts/packages/tauri-macos
