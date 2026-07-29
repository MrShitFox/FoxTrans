[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string] $Configuration = "Release",
    [ValidateSet("win-x64", "linux-x64")]
    [string] $RuntimeIdentifier,
    [string] $OutputDirectory = "artifacts\release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = $PSScriptRoot
$hostRid = if ($IsWindows -or $env:OS -eq "Windows_NT") {
    "win-x64"
}
elseif ($IsLinux) {
    "linux-x64"
}
else {
    throw "FoxTrans publishing supports Windows x64 and Linux x64 hosts only."
}

$rid = if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $hostRid
}
else {
    $RuntimeIdentifier
}
if ($rid -ne $hostRid) {
    throw "Native $rid assets must be published on a matching $rid host."
}

$outputRoot = [IO.Path]::GetFullPath(
    [IO.Path]::Combine($repositoryRoot, $OutputDirectory))
$ridRoot = [IO.Path]::Combine($outputRoot, $rid)
$desktopDirectory = [IO.Path]::Combine($ridRoot, "desktop")
$cliDirectory = [IO.Path]::Combine($ridRoot, "cli")

foreach ($directory in @($desktopDirectory, $cliDirectory)) {
    $resolvedParent = [IO.Path]::GetFullPath(
        [IO.Path]::GetDirectoryName($directory))
    if ($resolvedParent -ne $ridRoot) {
        throw "Refusing to clean a staging directory outside '$ridRoot'."
    }
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $directory | Out-Null
}

function Publish-FoxTrans([string] $Project, [string] $Destination) {
    dotnet publish `
        (Join-Path $repositoryRoot $Project) `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -o $Destination `
        -p:TrimmerSingleWarn=false
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for $Project."
    }
}

Publish-FoxTrans "FoxTrans.Desktop\FoxTrans.Desktop.csproj" $desktopDirectory
Publish-FoxTrans "FoxTrans.Cli\FoxTrans.Cli.csproj" $cliDirectory

$desktopExecutable = if ($rid -eq "win-x64") { "FoxTrans.exe" } else { "FoxTrans" }
$cliExecutable = if ($rid -eq "win-x64") { "FoxTrans.Cli.exe" } else { "FoxTrans.Cli" }
foreach ($expected in @(
        @{ Directory = $desktopDirectory; File = $desktopExecutable },
        @{ Directory = $cliDirectory; File = $cliExecutable })) {
    $actual = @(
        Get-ChildItem -LiteralPath $expected.Directory -File |
            Select-Object -ExpandProperty Name |
            Sort-Object)
    if (Compare-Object @($expected.File) $actual) {
        throw "Single-file publish did not contain exactly $($expected.File): $($actual -join ', ')."
    }
}

if ($rid -eq "win-x64") {
    $desktopArchive = Join-Path $outputRoot "FoxTrans-Desktop-$rid.zip"
    $cliArchive = Join-Path $outputRoot "FoxTrans-Cli-$rid.zip"
    Remove-Item -LiteralPath $desktopArchive, $cliArchive -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $desktopDirectory "*") -DestinationPath $desktopArchive
    Compress-Archive -Path (Join-Path $cliDirectory "*") -DestinationPath $cliArchive
}
else {
    $desktopArchive = Join-Path $outputRoot "FoxTrans-Desktop-$rid.tar.gz"
    $cliArchive = Join-Path $outputRoot "FoxTrans-Cli-$rid.tar.gz"
    Remove-Item -LiteralPath $desktopArchive, $cliArchive -Force -ErrorAction SilentlyContinue
    & tar -C $desktopDirectory -czf $desktopArchive $desktopExecutable
    if ($LASTEXITCODE -ne 0) { throw "Could not create $desktopArchive." }
    & tar -C $cliDirectory -czf $cliArchive $cliExecutable
    if ($LASTEXITCODE -ne 0) { throw "Could not create $cliArchive." }
}

Get-Item -LiteralPath $desktopArchive, $cliArchive |
    Select-Object FullName, Length
