# MyFramework Build Studio

Build Studio exposes one deterministic project contract to Unity menus, local GUI tooling and CI.

## Project structure

Use `MyFramework/Build Studio/生成项目结构文件` to create `MyFrameworkProject.json` at the Unity project root. The file is safe to commit: it contains project capabilities, exact package versions, scenes, managed-code boundaries, AssetBundle roots, build profiles and module declarations. It never contains passwords, tokens, private keys or machine-specific output paths.

When `ProjectSettings/AbCfg.asset` exists and the project has not supplied a custom AssetBundle profile, MyFramework automatically declares Windows, macOS, Android and iOS AssetBundle-only profiles. These profiles use the framework's built-in AssetBundle provider and do not require a project adapter, HybridCLR baseline, update URL or signing key. Build Studio creates a unique output under its local application-data `Outputs/<project>/<profile>/<job>` tree when no output directory is selected, so builds never dirty the Unity Git worktree.

Projects extend the generated document by registering `IMfProjectStructureContributor`. Production actions are registered separately through `IMfBuildProvider`, so the public contract remains generic while ArcadeHub, Fishing and later games keep their own production semantics.

The canonical SHA-256 in `structureHash` excludes only the hash field itself. Build jobs must carry the same hash; the Unity worker regenerates the live structure and rejects stale or edited contracts.

## Batch generation

```bash
Unity -batchmode -quit -projectPath /absolute/project \
  -executeMethod MyFramework.BuildStudio.Editor.MfProjectStructureCli.runCli \
  -mfStructureOutput /absolute/project/MyFrameworkProject.json \
  -logFile /absolute/logs/structure.log
```

## Build worker

The desktop application writes `BuildJob.json` outside the project and launches one Unity process per target:

```bash
Unity -batchmode -quit -projectPath /absolute/project \
  -buildTarget StandaloneWindows64 \
  -executeMethod MyFramework.BuildStudio.Editor.MfBuildCli.runCli \
  -mfJob /absolute/jobs/id/BuildJob.json \
  -mfReceipt /absolute/jobs/id/BuildReceipt.json \
  -mfEventFile /absolute/jobs/id/events.jsonl \
  -logFile /absolute/jobs/id/UnityEditor.log
```

Events are JSON Lines. The final receipt records stage durations, result identity, artifacts, sizes and SHA-256 values. Candidate/transaction behavior continues to be owned by `AbBuild`, `ProdFlow`, `PackFlow` and the project production adapter.

## Interactive Editor coordination

The GUI and `mf-build` may be used while the project is already open. Before structure generation or a build, the tool writes a one-time close request under `Temp/MyFrameworkBuildStudio`. The package bridge stops Play Mode if necessary, waits for compilation/import to finish, saves assets and open scenes, acknowledges the same token, and exits the interactive Editor. The worker starts only after the Unity lock disappears. A canceled save, concurrent request or two-minute timeout fails safely without force-quitting the Editor.

## Security

- Structure files and jobs reject unknown fields and secret-shaped property names.
- Project-relative paths cannot traverse outside the project.
- Build inputs, output directories and artifacts reject symbolic links.
- Arguments are passed through `ProcessStartInfo.ArgumentList`, never shell concatenation.
- Cancellation terminates the Unity process tree and records a canceled receipt.
- Signing passwords remain in process environment or OS credential storage; they are never serialized.
