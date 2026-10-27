[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    $release = '.build/Alpha-4.0'
    $vpk = '.build/Tools/vpk.exe'
    if (!(Test-Path -LiteralPath $vpk)) {
        dotnet tool install vpk --tool-path .build/Tools --version 1.3.39-g8d2844f
        if ($LASTEXITCODE -ne 0) { throw '安装打包工具失败。' }
    }
    dotnet publish App/AstralStation.csproj -c Release -r win-x64 --self-contained true -o "$release/App"
    if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
    [xml]$project = Get-Content -LiteralPath App/AstralStation.csproj
    $version = [string]$project.Project.PropertyGroup[0].Version
    # Rebuilding a failed/repaired version must not reuse an index for that same version.
    $buildRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.build'))
    $installer = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "$release/Installer"))
    if (!$installer.StartsWith($buildRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '输出目录越界。' }
    if (Test-Path -LiteralPath $installer) {
        if ((Get-Item -LiteralPath $installer).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '输出目录不能为链接。' }
        Remove-Item -LiteralPath $installer -Recurse -Force
    }
    & $vpk pack --packId SnowCup1024.AstralStation --packVersion $version --packTitle '星间驿站' --packAuthors SnowCup1024 --packDir "$release/App" --mainExe AstralStation.exe --runtime win-x64 --channel alpha --icon App/Assets/App.ico --outputDir "$release/Installer"
    if ($LASTEXITCODE -ne 0) { throw '安装器打包失败。' }
    Get-ChildItem "$release/Installer" -Filter '*Setup.exe' | Select-Object FullName,Length
} finally { Pop-Location }
