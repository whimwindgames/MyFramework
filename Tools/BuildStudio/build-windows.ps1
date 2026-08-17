param(
    [string]$Version = "0.1.0",
    [string]$DotNet = "dotnet"
)
$ErrorActionPreference = "Stop"
$studioRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$outputRoot = Join-Path $studioRoot "BuildOutput/BuildStudio/$Version/windows-x64"
if (Test-Path $outputRoot) { throw "Immutable output already exists: $outputRoot" }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$app = Join-Path $studioRoot "Tools/BuildStudio/src/MyFramework.BuildStudio.App/MyFramework.BuildStudio.App.csproj"
$cli = Join-Path $studioRoot "Tools/BuildStudio/src/MyFramework.BuildStudio.Cli/MyFramework.BuildStudio.Cli.csproj"
& $DotNet publish $app -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false -o $outputRoot
$cliOutput = Join-Path $outputRoot "cli"
& $DotNet publish $cli -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false -o $cliOutput
Copy-Item (Join-Path $cliOutput "mf-build.exe") (Join-Path $outputRoot "mf-build.exe")
Remove-Item -Recurse -Force $cliOutput
Write-Output $outputRoot
