#!/usr/bin/env bash
# macOS counterpart of tools/publish-tauri.cmd. See that file's header for
# the rollback-strategy rationale (Windows/.NET builds are untouched by
# this). Stages amm-mcp into src-tauri/resources (bundle.resources's
# wildcard sweeps it into the .app's Contents/Resources, same role as
# staging amm-mcp.exe for the Windows installer) and assembles a flat
# artifacts/publish/tauri-macos/out folder for manual testing without a
# .app/.dmg.
#
# Requires: cargo (`cargo install tauri-cli --version ^2` only needed for
# tools/build-installer-tauri-macos.sh, not this script - mirrors
# publish-tauri.cmd's own split).
#
# NOTE (open question, tasks.md 6.x follow-up): amm-mcp ends up inside the
# .app bundle at Contents/Resources/amm-mcp, which is NOT on $PATH and has
# no installer-driven PATH registration step the way the Windows NSIS/MSI
# installers have (tools/build-installer-tauri.cmd). hook_cli.rs/
# mcp_cli.rs's CLI self-registration (writing an `amm-mcp` command string
# into ~/.claude/settings.json etc.) will need an absolute path or a
# symlink strategy on macOS - not solved here, left for the feature-parity
# sweep (tasks.md 8) to decide.
set -euo pipefail

cd "$(dirname "$0")/.."

STAGE_DIR="src/apps/Amm/src-tauri/resources"

# Rebuild the resources stage fresh but keep .gitkeep so the directory
# stays tracked even with nothing else in it (mirrors publish-tauri.cmd).
find "$STAGE_DIR" -mindepth 1 -not -name ".gitkeep" -delete

rm -rf artifacts/publish/tauri-macos
mkdir -p artifacts/publish/tauri-macos/out

echo "=== Building amm GUI + amm-mcp CLI (Rust/Tauri) ==="
cargo build --release --manifest-path src/apps/Amm/src-tauri/Cargo.toml

# Sanity check ported from tools/build-installer-tauri.cmd (found
# 2026-07-26 on Windows, reproduced on macOS 2026-07-29 - see
# tasks/retro-pending.md): cargo/cargo-tauri has repeatedly been observed
# to leave artifacts/target/release/amm as a copy of amm-mcp's own output
# instead of the real (much larger, statically-linked-WebView) GUI binary.
check_amm_binary_size() {
  local label="$1"
  local amm_size amm_mcp_size
  if [[ ! -f artifacts/target/release/amm ]]; then
    echo "*** sanity check ($label): artifacts/target/release/amm not found ***" >&2
    exit 1
  fi
  amm_size=$(stat -f%z artifacts/target/release/amm)
  amm_mcp_size=$(stat -f%z artifacts/target/release/amm-mcp 2>/dev/null || echo 0)
  if (( amm_size < 5000000 )); then
    echo "*** sanity check ($label) FAILED: amm is ${amm_size} bytes, expected tens of MB (statically links Tauri/WebView/the frontend). This matches the amm/amm-mcp binary-swap corruption found on Windows and reproduced on macOS - delete artifacts/target/release/amm and artifacts/target/release/deps/amm-*, then rebuild. ***" >&2
    exit 1
  fi
  if [[ "$amm_size" == "$amm_mcp_size" ]]; then
    echo "*** sanity check ($label) FAILED: amm and amm-mcp are byte-identical in size (${amm_size}) - almost certainly the same file via the known cargo-tauri corruption bug. ***" >&2
    exit 1
  fi
}
check_amm_binary_size "after cargo build"

echo
echo "=== Staging amm-mcp ==="
cp artifacts/target/release/amm-mcp "$STAGE_DIR/"

echo
echo "=== Assembling artifacts/publish/tauri-macos/out ==="
cp artifacts/target/release/amm artifacts/publish/tauri-macos/out/
find "$STAGE_DIR" -mindepth 1 -not -name ".gitkeep" -exec cp {} artifacts/publish/tauri-macos/out/ \;
cp -R src/modules/Amm.PowerShell artifacts/publish/tauri-macos/out/Amm.PowerShell
# macOS delta (found alongside the artifacts/ restructure, 2026-08-04):
# this previously copied the Windows-formatted src/apps/Amm/profiles.amm
# (cmd.exe/claude.exe entries) into a macOS output folder. The real .app
# bundle gets the correct file via tauri.macos.conf.json's resource
# override (profiles.macos.amm -> profiles.amm inside Contents/Resources),
# but this flat out/ folder is assembled by hand and has no such override.
cp src/apps/Amm/profiles.macos.amm artifacts/publish/tauri-macos/out/profiles.amm
cp docs/manual/user-guide/usage.md artifacts/publish/tauri-macos/out/

echo
echo "*** publish-tauri-macos succeeded ***"
echo "Flat output:      $(pwd)/artifacts/publish/tauri-macos/out/"
echo "Installer stage:  $(pwd)/$STAGE_DIR/  (consumed by tauri.conf.json bundle.resources)"
echo
ls artifacts/publish/tauri-macos/out
