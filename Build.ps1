[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    dotnet publish App/AstralStation.csproj -c Release -r win-x64 --self-contained true -o .build/Alpha-3.9.3
    if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
    $publishedExe = (Resolve-Path -LiteralPath .build/Alpha-3.9.3/AstralStation.exe).Path
    $repair = Start-Process -FilePath $publishedExe -ArgumentList '--repair-login-startup' -WindowStyle Hidden -Wait -PassThru
    if ($repair.ExitCode -ne 0) { throw '构建成功，但已有的 Windows 登录启动项修复失败，请查看本地数据目录的 startup-error.log。' }
    Get-Item .build/Alpha-3.9.3/AstralStation.exe | Select-Object FullName,Length
} finally { Pop-Location }
