@echo off
REM Build the Tauri-based amm installer (NSIS + MSI), parallel to
REM tools\build-installer.cmd (the WiX/WinForms one). Kept separate on
REM purpose - see tools\publish-tauri.cmd's header for the rollback-strategy
REM rationale (both builds coexist until Tauri passes full parity).
REM
REM Steps:
REM   1. run tools\publish-tauri.cmd to build amm.exe/amm-mcp.exe (Rust)
REM   2. cargo tauri build to produce NSIS/MSI installers (always builds
REM      release mode by itself - passing --release is a cargo-tauri error).
REM      amm-mcp.exe is bundled automatically (tauri-cli's own src\bin
REM      binary discovery, see tauri.windows.conf.json/publish-tauri.cmd
REM      headers) - nothing needs manual staging into src-tauri\resources
REM      on Windows, unlike macOS (publish-tauri-macos.sh's own header).
REM Output: artifacts\target\release\bundle\{nsis,msi}\*
REM         copied to artifacts\packages\tauri-windows\
REM
REM Requires: cargo-tauri (`cargo install tauri-cli --version ^2`).
REM
REM NOTE: keep this file ASCII + CRLF and avoid parentheses in REM lines.

setlocal
cd /d "%~dp0.." || (
  echo *** failed to cd to repo root: "%~dp0.." ***
  exit /b 1
)

echo === Step 1/2: publish-tauri (stages .NET binaries, builds amm.exe) ===
call "%~dp0publish-tauri.cmd" %1
if errorlevel 1 (
  echo.
  echo *** publish-tauri.cmd failed ***
  exit /b 1
)

REM Sanity check found needed 2026-07-26 in real-machine verification: a
REM stale incremental build cache once caused cargo to uplift
REM target\release\amm.exe as a hard link to amm-mcp.exe's output instead
REM of its own, so the installed GUI silently ran the CLI's code path and
REM never showed a window. amm.exe statically links Tauri/WebView2/the
REM whole frontend and is always tens of MB larger than the CLI-only
REM amm-mcp.exe, so an equal-or-suspiciously-close size is a reliable and
REM cheap early warning. See tasks/retro-pending.md's 2026-07-26 entry.
call :CHECK_AMM_EXE_SIZE "after publish-tauri"
if errorlevel 1 exit /b 1

REM -v (debug level) is required to see WiX light.exe/candle.exe's own
REM stdout/stderr on failure: tauri-bundler's output_ok() helper captures
REM both but only emits them via log::debug!, which the CLI's default
REM (info) verbosity suppresses entirely - a bare "failed to run
REM ...\light.exe" with zero detail otherwise (found 2026-08-05 debugging
REM a CI-only MSI bundling failure, see tasks/retro-pending.md).
echo.
echo === Step 2/2: cargo tauri build (NSIS + MSI) ===
pushd src\apps\Amm\src-tauri
cargo tauri build -v
set "TAURI_BUILD_ERR=%ERRORLEVEL%"
popd

if not "%TAURI_BUILD_ERR%"=="0" (
  echo.
  echo *** cargo tauri build failed ***
  exit /b 1
)

REM cargo tauri build re-invokes cargo build --release internally, which
REM can re-trigger the same amm.exe link corruption the earlier check
REM guards against - re-check before bundling into packages\.
call :CHECK_AMM_EXE_SIZE "after cargo tauri build"
if errorlevel 1 exit /b 1

if not exist artifacts\packages\tauri-windows mkdir artifacts\packages\tauri-windows
copy /y "artifacts\target\release\bundle\nsis\*.exe" artifacts\packages\tauri-windows\ >nul 2>nul
copy /y "artifacts\target\release\bundle\msi\*.msi" artifacts\packages\tauri-windows\ >nul 2>nul

echo.
echo *** installer build succeeded ***
echo Output: %CD%\artifacts\packages\tauri-windows\
dir /b artifacts\packages\tauri-windows
endlocal
exit /b 0

:CHECK_AMM_EXE_SIZE
set "AMM_EXE_SIZE=0"
set "AMM_MCP_EXE_SIZE=0"
for %%A in ("artifacts\target\release\amm.exe") do set "AMM_EXE_SIZE=%%~zA"
for %%A in ("artifacts\target\release\amm-mcp.exe") do set "AMM_MCP_EXE_SIZE=%%~zA"
if "%AMM_EXE_SIZE%"=="0" (
  echo *** sanity check %~1: artifacts\target\release\amm.exe not found ***
  exit /b 1
)
if %AMM_EXE_SIZE% LSS 5000000 (
  echo *** sanity check %~1 FAILED: amm.exe is %AMM_EXE_SIZE% bytes, expected tens of MB ^(statically links Tauri/WebView2/the frontend^). This matches the amm-mcp.exe hard-link-corruption symptom found 2026-07-26 - delete artifacts\target\release\amm.exe and artifacts\target\release\deps\amm.exe, then rebuild. ***
  exit /b 1
)
if "%AMM_EXE_SIZE%"=="%AMM_MCP_EXE_SIZE%" (
  echo *** sanity check %~1 FAILED: amm.exe and amm-mcp.exe are byte-identical in size ^(%AMM_EXE_SIZE%^) - almost certainly the same file via a bad hard link. ***
  exit /b 1
)
exit /b 0
