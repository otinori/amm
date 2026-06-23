@echo off
REM Build the amm Windows Installer MSI.
REM Steps:
REM   1. run tools\publish.cmd to emit self-contained binaries to artifacts\publish
REM   2. dotnet build src\installer\wix\Amm.Installer.wixproj to produce the MSI
REM Output: artifacts\packages\amm-setup.msi
REM Optional version override:  tools\build-installer.cmd 0.2.0.0
REM NOTE: keep this file ASCII + CRLF and avoid parentheses in REM lines.

setlocal
cd /d "%~dp0.." || (
  echo *** failed to cd to repo root: "%~dp0.." ***
  exit /b 1
)

set "PRODUCT_VERSION=%~1"
if "%PRODUCT_VERSION%"=="" set "PRODUCT_VERSION=0.1.1.0"

echo === Step 1/3: publish self-contained binaries ===
call "%~dp0publish.cmd"
if errorlevel 1 (
  echo.
  echo *** publish.cmd failed ***
  exit /b 1
)

echo.
echo === Step 2/3: strip pdb and unused xml docs from artifacts\publish ===
del /q artifacts\publish\*.pdb 2>nul
del /q artifacts\publish\Microsoft.Web.WebView2.*.xml 2>nul

echo.
echo === Step 3/3: build MSI with WiX 5 ===
dotnet build src\installer\wix\Amm.Installer.wixproj -c Release -p:ProductVersion=%PRODUCT_VERSION%
if errorlevel 1 (
  echo.
  echo *** MSI build failed ***
  exit /b 1
)

echo.
echo *** installer build succeeded ***
echo Output: %CD%\artifacts\packages\amm-setup.msi
echo Version: %PRODUCT_VERSION%
endlocal
