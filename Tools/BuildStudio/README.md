# MyFramework Build Studio

Cross-platform local production console for Unity projects that expose `MyFrameworkProject.json`.

## Components

- `MyFramework.BuildStudio.Contracts`: shared Schema 1 POCO contracts linked directly from the Unity package.
- `MyFramework.BuildStudio.Core`: strict JSON/hash validation, Unity discovery, preflight, job execution, cancellation and SQLite history.
- `mf-build`: headless local/CI command line client.
- `MyFrameworkBuildStudio`: Avalonia Windows/macOS application.

## Development

```bash
dotnet restore Tools/BuildStudio/src/MyFramework.BuildStudio.App/MyFramework.BuildStudio.App.csproj
dotnet build Tools/BuildStudio/src/MyFramework.BuildStudio.App/MyFramework.BuildStudio.App.csproj -c Release
dotnet test Tools/BuildStudio/tests/MyFramework.BuildStudio.Core.Tests/MyFramework.BuildStudio.Core.Tests.csproj -c Release
```

Generate and validate a project structure:

```bash
mf-build generate --project /absolute/unity/project
mf-build scan --project /absolute/unity/project
mf-build preflight --project /absolute/unity/project --profile validate
```

Run a profile:

```bash
mf-build build --project /absolute/unity/project --profile base-windows \
  --env test --version 1.0.0 --build-number 100
```

Required project modules are selected automatically. Repeat `--module <id>` to opt into optional modules; an explicit selection cannot omit a required module or name an undeclared module.

The worker starts the exact Unity version declared by the structure, passes arguments without a shell, writes all transient files under the user's application-data directory and stores final receipts in SQLite history. Full Unity output remains in each job's `UnityEditor.log`; the GUI displays stage events and diagnostic log lines only.

## Distributions

On macOS:

```bash
Tools/BuildStudio/build-distributions.sh 0.1.0
```

This creates self-contained macOS ARM64/x64 `.app` bundles and a self-contained Windows x64 `.exe` distribution under `BuildOutput/BuildStudio/<version>`. macOS bundles are ad-hoc signed by default; set `MF_MAC_SIGN_IDENTITY` to a Developer ID Application identity for distributable signing and notarize the resulting ZIP in the release pipeline.

On Windows, `build-windows.ps1` produces the self-contained Windows x64 application and CLI.
