@{
    ModuleVersion     = '0.1.0'
    GUID              = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890'
    Author            = 'otinori'
    Description       = 'PowerShell cmdlets for controlling amm multi-agent operator console.'
    PowerShellVersion = '7.4'
    RootModule        = 'Amm.PowerShell.dll'
    FunctionsToExport = @()
    CmdletsToExport   = @(
        'Connect-Amm',
        'Disconnect-Amm',
        'Open-AmmWindow',
        'Close-AmmWindow',
        'Send-AmmMessage',
        'Get-AmmSession',
        'Wait-AmmIdle'
    )
    VariablesToExport = @()
    AliasesToExport   = @()
}
