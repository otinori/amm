@echo off
REM Self-contained single-exe publish for Windows x64.
REM Publish settings live in the repo-managed publish profiles src\publish\<app>\*.pubxml
REM so CI and local stay in sync. This script only orchestrates order and output dir.
REM Output goes to artifacts\publish :
REM   amm.exe       - GUI
REM   amm-mcp.exe   - MCP stdio bridge / CLI / REPL
REM Both apps are merged flat into artifacts\publish so the MSI can harvest the tree.
REM NOTE: keep this file ASCII + CRLF. cmd.exe breaks on UTF-8 multibyte comments
REM       and on LF line endings. Do NOT put parentheses in REM lines either.

setlocal
cd /d "%~dp0.." || (
  echo *** failed to cd to repo root: "%~dp0.." ***
  exit /b 1
)

REM Rebuild artifacts\publish from scratch every time. Incremental publish can skip
REM re-copying files deleted from the output by hand, and the MSI harvests the whole
REM publish tree, so a partial tree would drop files such as profiles.amm.
if exist artifacts\publish rd /s /q artifacts\publish

echo === Publishing amm GUI ===
dotnet publish src\apps\Amm\Amm.csproj ^
  -c Release ^
  -p:AmmPublishProfile=win-x64-singlefile ^
  -o artifacts\publish

if errorlevel 1 (
  echo.
  echo *** amm publish failed ***
  exit /b 1
)

echo.
echo === Publishing amm-mcp bridge / CLI ===
dotnet publish src\apps\Amm.Mcp\Amm.Mcp.csproj ^
  -c Release ^
  -p:AmmPublishProfile=win-x64-singlefile ^
  -o artifacts\publish

if errorlevel 1 (
  echo.
  echo *** amm-mcp publish failed ***
  exit /b 1
)

echo.
echo === Building Amm.PowerShell module ===
dotnet publish src\modules\Amm.PowerShell\Amm.PowerShell.csproj ^
  -c Release ^
  -o artifacts\publish

if errorlevel 1 (
  echo.
  echo *** Amm.PowerShell build failed ***
  exit /b 1
)

echo.
echo *** publish succeeded ***
echo Output: %CD%\artifacts\publish\
echo.
dir /b artifacts\publish
endlocal
