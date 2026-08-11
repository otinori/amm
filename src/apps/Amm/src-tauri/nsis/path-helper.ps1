# PATH add/remove helper invoked by hooks.nsh via nsExec, instead of doing
# this manipulation with NSIS's own native string functions.
#
# Root cause of the 2026-07-26 real-machine finding this replaces: NSIS's
# ReadRegStr/StrFunc.nsh operate on fixed-size string buffers
# (NSIS_MAX_STRLEN, ~1024 chars for the stock makensis.exe this project's
# build uses) and silently fail (return an empty string, not a truncated
# one) when the real value exceeds that size. A real machine's PATH
# commonly exceeds 1024 chars (confirmed: 1234 chars / 30 entries on the
# verification machine) - well within a normal range for any developer
# workstation with more than a handful of tools installed. The old logic
# would then treat the empty read as "PATH is empty", wiping the entire
# machine PATH on WriteRegExpandStr. PowerShell's registry access has no
# such length limit, so this script sidesteps the problem entirely rather
# than working around NSIS's buffer size.
#
# Usage: path-helper.ps1 -InstDir <path> -Mode add|remove
param(
  [Parameter(Mandatory = $true)][string]$InstDir,
  [Parameter(Mandatory = $true)][ValidateSet('add', 'remove')][string]$Mode
)

$ErrorActionPreference = 'Stop'

$keyPath = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
$key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($keyPath, $true)
if (-not $key) {
  Write-Error "could not open HKLM\$keyPath for write"
  exit 1
}

try {
  # DoNotExpandEnvironmentNames: read the raw (unexpanded) string, so any
  # %VARIABLE% entries other software put in PATH pass through untouched
  # instead of being expanded into their current values and baked in.
  $current = $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
  if ($null -eq $current) { $current = '' }

  $entries = @($current -split ';' | Where-Object { $_ -ne '' })
  $instDirTrimmed = $InstDir.TrimEnd('\')
  $alreadyPresent = $entries | Where-Object { $_.TrimEnd('\') -ieq $instDirTrimmed }

  if ($Mode -eq 'add') {
    if (-not $alreadyPresent) {
      $entries += $InstDir
    }
  } else {
    $entries = @($entries | Where-Object { -not ($_.TrimEnd('\') -ieq $instDirTrimmed) })
  }

  $newValue = [string]::Join(';', $entries)
  $key.SetValue('Path', $newValue, [Microsoft.Win32.RegistryValueKind]::ExpandString)
} finally {
  $key.Close()
}
