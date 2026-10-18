[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    dotnet publish App/AstralStation.csproj -c Release -r win-x64 --self-contained true -o .build/Alpha-2.1.2
    if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
    Get-Item .build/Alpha-2.1.2/AstralStation.exe | Select-Object FullName,Length
} finally { Pop-Location }
