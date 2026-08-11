; tasks.md 6.1's documented gap: the Tauri NSIS bundler has no built-in
; equivalent of the old WiX (.NET/WinForms) installer's PathEnvironment
; component (src/installer/wix/Package.wxs, where MSI's own Environment
; table handles add/remove natively). NSIS has no such built-in table, so
; this hand-implements the same net effect: append $INSTDIR to the system
; PATH on install, remove exactly that entry on uninstall.
;
; ⚠️ 2026-07-26 real-machine finding (see UDR-amm-20260726T1325-9d4 and
; tasks/retro-pending.md): the original implementation used NSIS's own
; ReadRegStr/StrFunc.nsh string functions directly against the real
; machine PATH. Those operate on fixed NSIS_MAX_STRLEN-sized buffers
; (~1024 chars for the stock makensis.exe this project's build uses) and
; FAIL SILENTLY - ReadRegStr returns an empty string, not a truncated one
; - once the real value exceeds that size. The verification machine's
; real PATH was 1234 chars (30 entries, an entirely ordinary developer
; workstation, nothing exotic), which was enough to trigger this: the
; uninstaller's un.RemoveFromPath read "" instead of the real PATH, took
; that as "PATH is empty, nothing to remove"... except the write-back
; path still fired and wrote that "" over the real value, WIPING THE
; ENTIRE MACHINE PATH on uninstall. Confirmed via a battery of isolated
; NSIS repros against a throwaway HKCU test key before touching the real
; key again; confirmed NSIS_MAX_STRLEN is a compiler-baked constant for
; this makensis.exe build, not overridable via `!define` from script
; ("!define: NSIS_MAX_STRLEN already defined!").
;
; Fix: delegate the actual PATH read/modify/write to PowerShell
; (path-helper.ps1) via nsExec instead of NSIS's own string functions.
; PowerShell's registry access (Microsoft.Win32.Registry) has no
; equivalent length limit, so this sidesteps the NSIS buffer-size problem
; entirely rather than trying to work within it. NSIS itself never holds
; the long PATH string in a variable at all now - only $INSTDIR (short)
; crosses the NSIS/PowerShell boundary as a command-line argument.
;
; Real-machine verified 2026-07-26 (after this fix): install correctly
; adds $INSTDIR to PATH (confirmed via a fresh-registry-read resolution,
; not a stale already-running shell's $env:PATH) and uninstall correctly
; removes exactly that entry, leaving the other 30 baseline PATH entries
; byte-for-byte intact (diffed against a pre-install snapshot).

!include "WinMessages.nsh"
!include "LogicLib.nsh"

!macro RunPathHelper Mode
  InitPluginsDir
  File "/oname=$PLUGINSDIR\amm-path-helper.ps1" "${__FILEDIR__}\path-helper.ps1"
  nsExec::ExecToStack '"powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\amm-path-helper.ps1" -InstDir "$INSTDIR" -Mode ${Mode}'
  Pop $0 ; exit code
  Pop $1 ; stack/output text
  ${If} $0 != 0
    DetailPrint "amm: PATH ${Mode} via PowerShell failed (exit $0): $1"
  ${EndIf}
  SendMessage ${HWND_BROADCAST} ${WM_SETTINGCHANGE} 0 "STR:Environment" /TIMEOUT=5000
!macroend

Function AddToPath
  !insertmacro RunPathHelper "add"
FunctionEnd

Function un.RemoveFromPath
  !insertmacro RunPathHelper "remove"
FunctionEnd

!macro NSIS_HOOK_POSTINSTALL
  Call AddToPath
!macroend

!macro NSIS_HOOK_PREUNINSTALL
  Call un.RemoveFromPath
!macroend
