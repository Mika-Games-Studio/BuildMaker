# Repository Guidelines

## Project Structure & Module Organization

- `src/UnityLocalCI.Core/` contains build orchestration, Git/Unity integrations, configuration, persistence, publishing, and notifications. Keep reusable logic here.
- `src/UnityLocalCI.Worker/` is the Windows entry point and WinForms/tray UI. Its root namespace is `UnityLocalCI.App`.
- `tests/UnityLocalCI.Tests/` contains the xUnit test suite for both projects.
- `unity/Builder.cs` is the Unity-side build entry point. `tools/` holds PowerShell scripts for publishing, installation, local testing, and manual triggers.
- `config/` contains configuration examples; `publicado/` is generated publication output and should not be treated as source.

## Build, Test, and Development Commands

Run commands from the repository root on Windows with the .NET 10 SDK installed.

```powershell
dotnet restore UnityLocalCI.slnx
dotnet build UnityLocalCI.slnx
dotnet test UnityLocalCI.slnx
dotnet run --project src/UnityLocalCI.Worker
powershell -ExecutionPolicy Bypass -File tools\testar-local.ps1
powershell -ExecutionPolicy Bypass -File tools\publicar.ps1
```

`dotnet run` starts the desktop app. `testar-local.ps1` creates a disposable end-to-end environment without touching a real Unity project. `publicar.ps1` tests and produces the self-contained distribution archive.

## Coding Style & Naming Conventions

Use four-space indentation and standard C# conventions: PascalCase for types and public members, camelCase for locals and parameters, and `I` prefixes for interfaces. Nullable reference types and implicit usings are enabled. Prefer file-scoped, focused classes grouped by feature directories. All compiler warnings are errors, so submit warning-free builds. Follow existing PowerShell naming and use approved verbs for new functions.

## Testing Guidelines

Tests use xUnit. Name test classes after the subject (`GitClientTests`) and test methods as readable behavior statements consistent with nearby tests. Add regression tests for bug fixes and unit tests for new Core or UI-support behavior. Run `dotnet test UnityLocalCI.slnx` before every pull request; no numeric coverage threshold is currently enforced.

## Commit & Pull Request Guidelines

Recent commits use short, sentence-style Portuguese summaries describing the user-visible result, for example `A aba GitHub mostra quem esta conectado`. Keep commits focused and avoid vague messages such as `ajustes`.

Pull requests should explain the problem and solution, list validation commands, and link the relevant issue. Include screenshots for WinForms changes and call out configuration, credential, installer, or Unity-version impacts. Never commit PATs, passwords, machine-specific paths, generated archives, or local runtime state.
