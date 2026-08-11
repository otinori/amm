@{
    ModuleVersion     = '0.1.0'
    GUID              = 'b2c3d4e5-f6a7-4901-bcde-f12345678901'
    Author            = 'otinori'
    Description       = 'PowerShell cmdlets for controlling the Tauri-based amm multi-agent operator console (script module, no compiled binary - see UDR-amm-20260719T0013-b7e).'
    PowerShellVersion = '5.1'
    RootModule        = 'Amm.PowerShell.psm1'
    FunctionsToExport = @(
        'Connect-Amm',
        'Disconnect-Amm',
        'Open-AmmWindow',
        'Close-AmmWindow',
        'Send-AmmMessage',
        'Get-AmmSession',
        'Wait-AmmIdle'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
