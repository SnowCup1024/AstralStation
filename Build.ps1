[CmdletBinding()]
param([string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    [xml]$metadata = Get-Content -LiteralPath Directory.Build.props
    $display = [string]$metadata.Project.PropertyGroup.AstralDisplayVersion
    $version = [string]$metadata.Project.PropertyGroup.Version
    $revision = [int]($version -replace '^.*-alpha\.', '')
    $buildRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.build'))
    $stage = Join-Path $buildRoot 'Stage/App'
    $release = Join-Path $buildRoot ('Alpha-' + $display)
    if (!$InnoCompiler) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $InnoCompiler = $command.Source }
        elseif (Test-Path -LiteralPath (Join-Path $buildRoot 'Tools/Inno/ISCC.exe')) {
            $InnoCompiler = Join-Path $buildRoot 'Tools/Inno/ISCC.exe'
        } else { throw '请准备官方 Inno Setup 7.1 编译器，并通过 -InnoCompiler 指定 ISCC.exe。构建脚本不会下载安装工具。' }
    }
    if (!(Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) { throw '找不到 Inno Setup 编译器。' }
    function Clear-Generated([string]$path) {
        $absolute = [IO.Path]::GetFullPath($path)
        if (!$absolute.StartsWith($buildRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '输出目录越界。' }
        if (Test-Path -LiteralPath $absolute) {
            $cursor = Get-Item -LiteralPath $absolute
            while ($cursor -and $cursor.FullName.StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase)) {
                if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '输出目录不能经过链接。' }
                $cursor = $cursor.Parent
            }
            Remove-Item -LiteralPath $absolute -Recurse -Force
        }
    }
    Clear-Generated $stage
    Clear-Generated $release
    dotnet publish App/AstralStation.csproj -c Release -r win-x64 --self-contained true -o $stage
    if ($LASTEXITCODE -ne 0) { throw '应用发布失败。' }
    dotnet publish Monitor/AstralStation.Monitor.csproj -c Release -r win-x64 --self-contained true -o (Join-Path $stage 'Monitor')
    if ($LASTEXITCODE -ne 0) { throw '后台发布失败。' }
    & $InnoCompiler ('/DSourcePath='+$stage) ('/DOutputPath='+$release) ('/DDisplayVersion='+$display) ('/DFileVersion=1.0.0.'+$revision) ('/DAppIcon='+(Join-Path $PSScriptRoot 'App/Assets/App.ico')) (Join-Path $PSScriptRoot 'Installer/Setup.iss')
    if ($LASTEXITCODE -ne 0) { throw '安装器构建失败。' }
    Get-Item -LiteralPath (Join-Path $release ('AstralStation-Alpha-'+$display+'-Setup.exe')) | Select-Object FullName,Length
} finally { Pop-Location }
