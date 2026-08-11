#Requires -Version 5.1
<#
  PowerShell script module port of src/modules/Amm.PowerShell.net (binary module,
  System.Management.Automation cmdlets in C#), per UDR-amm-20260719T0013-b7e:
  eliminates the .NET SDK build dependency entirely - this file needs no
  compilation, only the .NET types PowerShell's own runtime already provides
  (System.IO.Pipes.NamedPipeClientStream), same as calling into cmd.exe from
  a script is not considered an added dependency.

  Kept as a SEPARATE module (folder name Amm.PowerShell, cutover from the
  Tauri-suffixed name; the old .NET binary module now lives at
  Amm.PowerShell.net) per the rollback strategy: the old .NET GUI keeps
  using the old binary module against amm.openWindow/amm.closeWindow,
  this one targets the Rust GUI's amm.openPane/amm.closePane (renamed per
  UDR-amm-20260713T0447-98f). Exported cmdlet *names* are identical so
  scripts written against either module work unchanged; only the
  Import-Module name differs during the transition.

  Targets Windows PowerShell 5.1+ (the old binary module required 7.4, but
  that was purely a consequence of it targeting net9.0 for compilation -
  since this is a script with nothing to compile, there's no reason to
  require newer-than-default-Windows-ships-with PowerShell). Verified
  against the actual PowerShell 5.1.19041.3930 on the authoring machine.
#>

Set-StrictMode -Version Latest

# ---- pipe client (script port of Amm.PowerShell/Pipe/AmmPipeClient.cs) ----

function Get-AmmDefaultPipeName {
  if ($env:AMM_MCP_PIPE_NAME) { return $env:AMM_MCP_PIPE_NAME }
  return "amm-mcp-$env:USERNAME"
}

function Open-AmmPipeConnection {
  param(
    [string] $PipeName,
    [int] $ConnectTimeoutMs = 5000
  )
  $name = if ($PipeName) { $PipeName } else { Get-AmmDefaultPipeName }
  $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
    '.', $name, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
  try {
    $pipe.Connect($ConnectTimeoutMs)
  } catch [TimeoutException] {
    $pipe.Dispose()
    throw [System.InvalidOperationException]::new(
      "amm-mcp: amm GUI に接続できませんでした (pipe=$name)。GUI を起動してから再試行してください。")
  }
  $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
  $writer = [System.IO.StreamWriter]::new($pipe, $utf8NoBom, 4096, $true)
  $writer.AutoFlush = $true
  $writer.NewLine = "`n"
  $reader = [System.IO.StreamReader]::new($pipe, $utf8NoBom, $false, 4096, $true)
  return [PSCustomObject]@{
    PSTypeName = 'Amm.PipeConnection'
    Pipe       = $pipe
    Writer     = $writer
    Reader     = $reader
    NextId     = 0
  }
}

function Close-AmmPipeConnection {
  param([Parameter(Mandatory)] $Connection)
  try { $Connection.Writer.Dispose() } catch {}
  try { $Connection.Reader.Dispose() } catch {}
  try { $Connection.Pipe.Dispose() } catch {}
}

# Sends a raw JSON-RPC request (no MCP initialize handshake) and returns the
# parsed response object. Throws on a JSON-RPC "error" response, matching
# AmmPipeClient.SendRequest's contract exactly.
function Send-AmmPipeRequest {
  param(
    [Parameter(Mandatory)] $Connection,
    [Parameter(Mandatory)] [string] $Method,
    [object] $Params,
    [int] $ReadTimeoutMs = 0
  )
  $Connection.NextId++
  $req = [ordered]@{ jsonrpc = '2.0'; id = $Connection.NextId; method = $Method }
  if ($null -ne $Params) { $req.params = $Params }
  $Connection.Writer.WriteLine(($req | ConvertTo-Json -Depth 10 -Compress))

  $line = $null
  if ($ReadTimeoutMs -gt 0) {
    $task = $Connection.Reader.ReadLineAsync()
    if ($task.Wait($ReadTimeoutMs)) { $line = $task.Result }
    else { throw [System.InvalidOperationException]::new('amm-mcp: timed out waiting for response.') }
  } else {
    $line = $Connection.Reader.ReadLine()
  }

  if ($null -eq $line) {
    throw [System.InvalidOperationException]::new('amm-mcp: pipe closed before response arrived.')
  }
  $resp = $line | ConvertFrom-Json
  if ($resp.PSObject.Properties.Match('error').Count -gt 0 -and $null -ne $resp.error) {
    $msg = if ($resp.error.message) { $resp.error.message } else { 'unknown error' }
    throw [System.InvalidOperationException]::new("amm-mcp: server error: $msg")
  }
  return $resp
}

function Get-AmmPipeResult {
  param(
    [Parameter(Mandatory)] $Connection,
    [Parameter(Mandatory)] [string] $Method,
    [object] $Params,
    [int] $ReadTimeoutMs = 0
  )
  $resp = Send-AmmPipeRequest -Connection $Connection -Method $Method -Params $Params -ReadTimeoutMs $ReadTimeoutMs
  return $resp.result
}

# MCP tools/call wrapper: initialize (response discarded) then tools/call,
# matching Program.cs's CallToolAsync / the cmdlets that call "tools/call"
# directly (Send-AmmMessage, Get-AmmSession, Wait-AmmIdle's nickname lookup).
function Invoke-AmmToolCall {
  param(
    [Parameter(Mandatory)] $Connection,
    [Parameter(Mandatory)] [string] $ToolName,
    [object] $Arguments = @{}
  )
  Send-AmmPipeRequest -Connection $Connection -Method 'initialize' -Params ([ordered]@{
    protocol_version = '2024-11-05'
    capabilities     = @{}
    client_info      = [ordered]@{ name = 'Amm.PowerShell'; version = '0.1.0' }
  }) | Out-Null
  return Get-AmmPipeResult -Connection $Connection -Method 'tools/call' -Params ([ordered]@{
    name      = $ToolName
    arguments = $Arguments
  })
}

# tools/call results carry the payload under result.structuredContent (with
# a JSON-text duplicate under result.content[0].text as a fallback) - mirrors
# the .NET cmdlets' "structuredContent, else parse content[0].text" pattern.
function Get-AmmStructuredContent {
  param([Parameter(Mandatory)] $ToolResult)
  if ($ToolResult.PSObject.Properties.Match('structuredContent').Count -gt 0 -and $null -ne $ToolResult.structuredContent) {
    return $ToolResult.structuredContent
  }
  if ($ToolResult.content -and $ToolResult.content.Count -gt 0) {
    try { return $ToolResult.content[0].text | ConvertFrom-Json } catch { return $null }
  }
  return $null
}

# ---- Connect-Amm / Disconnect-Amm ----

function Connect-Amm {
  [CmdletBinding()]
  param(
    [string] $PipeName,
    [int] $ConnectTimeoutMs = 5000
  )
  try {
    $conn = Open-AmmPipeConnection -PipeName $PipeName -ConnectTimeoutMs $ConnectTimeoutMs
    Close-AmmPipeConnection -Connection $conn
    Write-Verbose "amm に接続しました (pipe=$(if ($PipeName) { $PipeName } else { Get-AmmDefaultPipeName }))。"
  } catch {
    $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
      [System.InvalidOperationException]::new('amm GUI に接続できませんでした。amm を起動してから再試行してください。'),
      'AmmNotRunning',
      [System.Management.Automation.ErrorCategory]::ConnectionError,
      $null))
  }
}

function Disconnect-Amm {
  [CmdletBinding()]
  param()
  Write-Verbose 'Disconnect-Amm: 現在の実装では各コマンドが接続を個別に管理するため操作不要です。'
}

# ---- Open-AmmWindow ----

function Open-AmmWindow {
  [CmdletBinding(DefaultParameterSetName = 'ByCommand')]
  [OutputType('Amm.Session')]
  param(
    [Parameter(Mandatory, Position = 0, ParameterSetName = 'ByCommand')] [string] $Command,
    [Parameter(Mandatory, Position = 0, ParameterSetName = 'ByProfile')] [string] $ProfileName,
    [Parameter(ParameterSetName = 'ByCommand')] [string[]] $Args = @(),
    [string] $Title,
    [string] $WorkingDirectory,
    [string] $PipeName,
    [int] $ConnectTimeoutMs = 5000
  )
  $target = if ($PSCmdlet.ParameterSetName -eq 'ByProfile') { $ProfileName } else { $Command }
  try {
    $conn = Open-AmmPipeConnection -PipeName $PipeName -ConnectTimeoutMs $ConnectTimeoutMs
    try {
      $params = [ordered]@{
        command            = if ($PSCmdlet.ParameterSetName -eq 'ByCommand') { $Command } else { $null }
        profile_name       = if ($PSCmdlet.ParameterSetName -eq 'ByProfile') { $ProfileName } else { $null }
        args               = $Args
        title              = $Title
        working_directory  = $WorkingDirectory
      }
      $result = Get-AmmPipeResult -Connection $conn -Method 'amm.openPane' -Params $params
    } finally {
      Close-AmmPipeConnection -Connection $conn
    }
  } catch {
    # 接続自体の失敗(GUI未起動等)は他のcmdlet群と同様、終了エラーにしない。
    Write-Error -Exception $_.Exception -Category ConnectionError -TargetObject $target -ErrorId 'OpenAmmWindowFailed'
    return
  }

  # spec: ps-module「session_id が空、または result 自体が得られない場合は
  # 終了エラーとする」- .NET原本のThrowTerminatingError契約(OpenAmmWindowFailed)
  # を再現する(found downgraded to non-terminating Write-Error in the
  # source-diff parity audit - a script relying on the pipeline stopping here
  # would silently continue past this failure instead).
  if ((-not $result) -or ($result.PSObject.Properties.Match('error').Count -gt 0 -and $result.error) -or (-not $result.session_id)) {
    $msg = if (-not $result) { 'amm-mcp: no result returned from amm.openPane' }
      elseif ($result.error) { "amm: $($result.error)" }
      else { 'amm-mcp: session_id not returned' }
    $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
      [System.InvalidOperationException]::new($msg), 'OpenAmmWindowFailed',
      [System.Management.Automation.ErrorCategory]::InvalidResult, $target))
  }

  $displayTitle = if ($Title) { $Title } elseif ($PSCmdlet.ParameterSetName -eq 'ByProfile') { $ProfileName } else { $Command }
  [PSCustomObject]@{
    PSTypeName = 'Amm.Session'
    SessionId  = $result.session_id
    Title      = $displayTitle
  }
}

# ---- Close-AmmWindow ----

function Close-AmmWindow {
  [CmdletBinding(SupportsShouldProcess)]
  param(
    [Parameter(Mandatory, Position = 0, ValueFromPipeline, ValueFromPipelineByPropertyName)]
    [string] $SessionId,
    [switch] $Force,
    [string] $PipeName,
    [int] $ConnectTimeoutMs = 5000
  )
  process {
    if (-not $PSCmdlet.ShouldProcess($SessionId, 'Close-AmmWindow')) { return }
    try {
      $conn = Open-AmmPipeConnection -PipeName $PipeName -ConnectTimeoutMs $ConnectTimeoutMs
      try {
        $result = Get-AmmPipeResult -Connection $conn -Method 'amm.closePane' -Params ([ordered]@{
          session_id = $SessionId
          force      = [bool] $Force
        })
      } finally {
        Close-AmmPipeConnection -Connection $conn
      }
      if ($result.PSObject.Properties.Match('error').Count -gt 0 -and $result.error) {
        Write-Warning "Close-AmmWindow: $($result.error)"
      }
    } catch {
      Write-Error -Exception $_.Exception -Category ConnectionError -TargetObject $SessionId -ErrorId 'CloseAmmWindowFailed'
    }
  }
}

# ---- Send-AmmMessage ----

function Send-AmmMessage {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory, Position = 0, ValueFromPipeline, ValueFromPipelineByPropertyName)]
    [Alias('Title')]
    [string] $Nickname,
    [Parameter(Mandatory, Position = 1)] [string] $Message,
    [string] $Mode = 'first',
    [string] $PipeName,
    [int] $ConnectTimeoutMs = 5000
  )
  process {
    try {
      $conn = Open-AmmPipeConnection -PipeName $PipeName -ConnectTimeoutMs $ConnectTimeoutMs
      try {
        $toolResult = Invoke-AmmToolCall -Connection $conn -ToolName 'send_message' -Arguments ([ordered]@{
          recipient = $Nickname
          message   = $Message
          mode      = $Mode
        })
      } finally {
        Close-AmmPipeConnection -Connection $conn
      }
      $sc = Get-AmmStructuredContent -ToolResult $toolResult
      $delivered = if ($sc -and $sc.PSObject.Properties.Match('delivered_count').Count -gt 0) { $sc.delivered_count } else { 0 }
      $queued = if ($sc -and $sc.PSObject.Properties.Match('queued_count').Count -gt 0) { $sc.queued_count } else { 0 }
      Write-Verbose "Send-AmmMessage: delivered=$delivered, queued=$queued"
    } catch {
      Write-Error -Exception $_.Exception -Category ConnectionError -TargetObject $Nickname -ErrorId 'SendAmmMessageFailed'
    }
  }
}

# ---- Get-AmmSession ----

function Get-AmmSession {
  [CmdletBinding()]
  [OutputType('Amm.Session')]
  param(
    [string] $PipeName,
    [int] $ConnectTimeoutMs = 5000
  )
  try {
    $conn = Open-AmmPipeConnection -PipeName $PipeName -ConnectTimeoutMs $ConnectTimeoutMs
    try {
      $toolResult = Invoke-AmmToolCall -Connection $conn -ToolName 'list_participants' -Arguments @{}
    } finally {
      Close-AmmPipeConnection -Connection $conn
    }
    $sc = Get-AmmStructuredContent -ToolResult $toolResult
    $participants = if ($sc -and $sc.participants) { $sc.participants } else { @() }
    foreach ($p in $participants) {
      $nickname = $p.nickname
      $instance = if ($p.PSObject.Properties.Match('instance').Count -gt 0) { $p.instance } else { 1 }
      $title = if ($instance -eq 1) { $nickname } else { "$nickname ($instance)" }
      [PSCustomObject]@{
        PSTypeName = 'Amm.Session'
        SessionId  = $p.session_id
        Title      = $title
      }
    }
  } catch {
    Write-Error -Exception $_.Exception -Category ConnectionError -ErrorId 'GetAmmSessionFailed'
  }
}

# ---- Wait-AmmIdle ----

function Resolve-AmmNicknameSessionId {
  param(
    [Parameter(Mandatory)] [string] $Nickname,
    [string] $PipeName,
    [int] $ConnectTimeoutMs
  )
  $conn = Open-AmmPipeConnection -PipeName $PipeName -ConnectTimeoutMs $ConnectTimeoutMs
  try {
    $toolResult = Invoke-AmmToolCall -Connection $conn -ToolName 'list_participants' -Arguments @{}
  } finally {
    Close-AmmPipeConnection -Connection $conn
  }
  $sc = Get-AmmStructuredContent -ToolResult $toolResult
  $participants = if ($sc -and $sc.participants) { $sc.participants } else { @() }
  $match = $participants | Where-Object { $_.nickname -ieq $Nickname } | Select-Object -First 1
  if (-not $match) {
    throw [System.InvalidOperationException]::new("amm: nickname '$Nickname' が見つかりません")
  }
  if (-not $match.session_id) {
    throw [System.InvalidOperationException]::new("amm: '$Nickname' に session_id がありません")
  }
  return $match.session_id
}

function Wait-AmmIdle {
  [CmdletBinding(DefaultParameterSetName = 'BySessionId')]
  [OutputType('Amm.WaitResult')]
  param(
    [Parameter(ParameterSetName = 'BySessionId', Mandatory, Position = 0, ValueFromPipeline, ValueFromPipelineByPropertyName)]
    [string] $SessionId,
    [Parameter(ParameterSetName = 'ByNickname', Mandatory, Position = 0)]
    [string] $Nickname,
    [string] $TargetState = 'idle',
    [int] $TimeoutMs = 300000,
    [string] $PipeName,
    [int] $ConnectTimeoutMs = 5000
  )
  process {
    $target = if ($PSCmdlet.ParameterSetName -eq 'ByNickname') { $Nickname } else { $SessionId }

    if ($PSCmdlet.ParameterSetName -eq 'ByNickname') {
      try {
        $resolvedSessionId = Resolve-AmmNicknameSessionId -Nickname $Nickname -PipeName $PipeName -ConnectTimeoutMs $ConnectTimeoutMs
      } catch {
        # spec: ps-module「見つからない場合は終了エラーとしなければならない
        # (MUST)」- found downgraded to non-terminating Write-Error in the
        # source-diff parity audit.
        $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
          $_.Exception, 'WaitAmmIdleNicknameNotFound',
          [System.Management.Automation.ErrorCategory]::ObjectNotFound, $target))
      }
    } else {
      $resolvedSessionId = $SessionId
    }

    try {
      $conn = Open-AmmPipeConnection -PipeName $PipeName -ConnectTimeoutMs $ConnectTimeoutMs
      try {
        # ReadTimeoutMs=0: the server withholds the response until the
        # target state is reached, so this waits unboundedly on the
        # client side too; TimeoutMs governs the server-side timeout.
        $result = Get-AmmPipeResult -Connection $conn -Method 'amm.waitState' -Params ([ordered]@{
          session_id   = $resolvedSessionId
          target_state = $TargetState
          timeout_ms   = $TimeoutMs
        }) -ReadTimeoutMs 0
      } finally {
        Close-AmmPipeConnection -Connection $conn
      }
    } catch {
      # 接続自体の失敗は他のcmdlet群と同様、終了エラーにしない。
      Write-Error -Exception $_.Exception -Category ConnectionError -TargetObject $target -ErrorId 'WaitAmmIdleFailed'
      return
    }

    if ((-not $result) -or (-not $result.PSObject.Properties.Match('state').Count)) {
      $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
        [System.InvalidOperationException]::new('amm-mcp: state missing in waitState response'),
        'WaitAmmIdleFailed', [System.Management.Automation.ErrorCategory]::InvalidResult, $target))
    }
    $elapsedMs = if ($result.PSObject.Properties.Match('elapsed_ms').Count -gt 0) { $result.elapsed_ms } else { 0 }
    [PSCustomObject]@{
      PSTypeName = 'Amm.WaitResult'
      State      = $result.state
      ElapsedMs  = $elapsedMs
    }
  }
}

Export-ModuleMember -Function @(
  'Connect-Amm', 'Disconnect-Amm', 'Open-AmmWindow', 'Close-AmmWindow',
  'Send-AmmMessage', 'Get-AmmSession', 'Wait-AmmIdle'
)
