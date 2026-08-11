@echo off
REM Tauri-based publish, parallel to tools\publish.cmd (the WinForms one).
REM Kept as a SEPARATE script/output dir on purpose: migrate-to-tauri's
REM rollback strategy keeps the WinForms build alive side by side until the
REM Tauri build passes full parity verification, so this must not touch
REM artifacts\publish\legacy or overwrite anything the WinForms path produces.
REM
REM Per UDR-amm-20260719T0013-b7e, amm-mcp and Amm.PowerShell no longer need
REM .NET at all: amm-mcp.exe is built by the same `cargo build` as amm.exe
REM (src-tauri\src\bin\amm-mcp\ is an implicit second Cargo binary target),
REM and Amm.PowerShell is a plain PowerShell script module with nothing to
REM compile - tauri.conf.json's bundle.resources references it straight
REM from src\modules\Amm.PowerShell (same as profiles.amm and usage.md are
REM referenced from their own source locations).
REM
REM This script does NOT stage amm-mcp.exe into src-tauri\resources: tauri-
REM cli's RustAppSettings::get_binaries() already auto-discovers every
REM binary under src-tauri\src\bin\ (confirmed against tauri-cli 2.11.4's
REM own source) and bundles amm-mcp.exe next to amm.exe on its own. An
REM earlier version of this script staged a redundant copy into resources\,
REM which made bundle.resources' `resources/*` glob harvest it too,
REM producing a SECOND WiX component for the same target file (alongside
REM tauri-cli's own auto-generated "amm_mcp" component) - light.exe's ICE30
REM check then failed the whole MSI with a bare "failed to run light.exe"
REM (the actual ICE30 detail is only visible with `cargo tauri build -v`;
REM found 2026-08-05 real-CI investigation, see tasks/retro-pending.md).
REM tauri.windows.conf.json now nulls out the `resources/*` entry entirely
REM (nothing is ever staged there on Windows, and an empty glob match is a
REM hard build.rs error). macOS needs the opposite: its own
REM publish-tauri-macos.sh independently stages amm-mcp into resources\
REM because commands_misc.rs's resolve_mcp_exe_path() looks for it at
REM Contents/Resources/amm-mcp there, not next to amm in Contents/MacOS/ -
REM that script/tauri.macos.conf.json are untouched by this file's change.
REM This script also assembles a flat artifacts\publish\tauri-windows\out folder
REM (amm.exe + amm-mcp.exe + the PowerShell module + profiles.amm +
REM usage.md) for zip-style/manual testing without running the installer.
REM
REM Requires: cargo + the tauri-cli (`cargo install tauri-cli --version ^2`)
REM on PATH as `cargo tauri` for tools\build-installer-tauri.cmd (this
REM script itself only needs `cargo build`). Not installed/verified in the
REM authoring session (see tasks\pending-real-machine-verification.md).
REM
REM NOTE: keep this file ASCII + CRLF. cmd.exe breaks on UTF-8 multibyte
REM       comments and on LF line endings. Do NOT put parentheses in REM
REM       lines either.
REM
REM Optional 1st arg: version override. Currently unused (amm-mcp's version
REM comes from tauri.conf.json / Cargo.toml, same as amm.exe - see
REM tasks.md 6.4) but kept for interface parity with publish.cmd.

setlocal
cd /d "%~dp0.." || (
  echo *** failed to cd to repo root: "%~dp0.." ***
  exit /b 1
)

if exist artifacts\publish\tauri-windows rd /s /q artifacts\publish\tauri-windows
mkdir artifacts\publish\tauri-windows\out

echo === Building amm GUI + amm-mcp CLI (Rust/Tauri) ===
cargo build --release --manifest-path src\apps\Amm\src-tauri\Cargo.toml
if errorlevel 1 (
  echo.
  echo *** cargo build failed ***
  exit /b 1
)

echo.
echo === Assembling artifacts\publish\tauri-windows\out ===
copy /y artifacts\target\release\amm.exe artifacts\publish\tauri-windows\out\ >nul
copy /y artifacts\target\release\amm-mcp.exe artifacts\publish\tauri-windows\out\ >nul
xcopy /y /i /e src\modules\Amm.PowerShell artifacts\publish\tauri-windows\out\Amm.PowerShell\ >nul
copy /y src\apps\Amm\profiles.amm artifacts\publish\tauri-windows\out\ >nul
copy /y docs\manual\user-guide\usage.md artifacts\publish\tauri-windows\out\ >nul

echo.
echo *** publish-tauri succeeded ***
echo Flat output:      %CD%\artifacts\publish\tauri-windows\out\
echo.
dir /b artifacts\publish\tauri-windows\out
endlocal
