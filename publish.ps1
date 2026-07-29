[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string] $Configuration = "Release",
    [string] $OutputDirectory = "artifacts\release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = $PSScriptRoot
$outputRoot = [IO.Path]::GetFullPath(
    [IO.Path]::Combine($repositoryRoot, $OutputDirectory))
$desktopDirectory = [IO.Path]::Combine($outputRoot, "desktop")
$cliDirectory = [IO.Path]::Combine($outputRoot, "cli")

foreach ($directory in @($desktopDirectory, $cliDirectory)) {
    $resolvedParent = [IO.Path]::GetFullPath(
        [IO.Path]::GetDirectoryName($directory))
    if ($resolvedParent -ne $outputRoot) {
        throw "Refusing to clean a staging directory outside '$outputRoot'."
    }
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $directory | Out-Null
}

dotnet publish `
    (Join-Path $repositoryRoot "FoxTrans.Desktop\FoxTrans.Desktop.csproj") `
    -c $Configuration `
    -o $desktopDirectory `
    -p:TrimmerSingleWarn=false
if ($LASTEXITCODE -ne 0) {
    throw "Desktop publish failed."
}

dotnet publish `
    (Join-Path $repositoryRoot "FoxTrans.Cli\FoxTrans.Cli.csproj") `
    -c $Configuration `
    -o $cliDirectory `
    -p:TrimmerSingleWarn=false
if ($LASTEXITCODE -ne 0) {
    throw "CLI publish failed."
}

$actualDesktopFiles = @(
    Get-ChildItem -LiteralPath $desktopDirectory -File |
        Select-Object -ExpandProperty Name |
        Sort-Object
)
if (Compare-Object @("FoxTrans.exe") $actualDesktopFiles) {
    throw "Desktop publish did not contain exactly one executable: $($actualDesktopFiles -join ', ')."
}

$actualCliFiles = @(
    Get-ChildItem -LiteralPath $cliDirectory -File |
        Select-Object -ExpandProperty Name |
        Sort-Object
)
if (Compare-Object @("FoxTrans.Cli.exe") $actualCliFiles) {
    throw "CLI publish did not contain exactly one executable: $($actualCliFiles -join ', ')."
}

$desktopZip = Join-Path $outputRoot "FoxTrans-Desktop-win-x64.zip"
$cliZip = Join-Path $outputRoot "FoxTrans-Cli-win-x64.zip"
Remove-Item -LiteralPath $desktopZip, $cliZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $desktopDirectory "*") -DestinationPath $desktopZip
Compress-Archive -Path (Join-Path $cliDirectory "*") -DestinationPath $cliZip

Get-Item -LiteralPath $desktopZip, $cliZip |
    Select-Object FullName, Length
