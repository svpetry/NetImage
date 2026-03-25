# AGENTS.md - NetImage Project Guide

Read this file before making changes.

## Project Summary

NetImage is a Windows desktop app for opening FAT disk images (`.ima`, `.img`), browsing their
contents, modifying them, and saving the result back to disk.

- Language: C# 13 with nullable enabled and implicit usings enabled
- Framework: .NET 10 (`net10.0-windows`)
- UI: WPF
- Architecture: MVVM
- Testing: NUnit

## Important Areas

You usually only need to know these parts of the codebase:

- `Workers/DiskImageWorker.cs`: FAT12/FAT16 parsing, reading, extraction, add/delete/create-folder,
  and save logic
- `ViewModels/MainViewModel.cs`: main application state, commands, and UI coordination
- `Models/TreeItem.cs`: tree/list item model used by the UI
- `Views/`: WPF windows and dialogs; keep code-behind minimal
- `Utils/ActionCommand.cs`: the project's `ICommand` implementation
- `NetImage.Tests/`: NUnit tests, mostly focused on `DiskImageWorker`

## MVVM Rules

- Keep business logic out of WPF code-behind.
- ViewModels must not reference `System.Windows` or other WPF UI types.
- Expose commands as `ActionCommand`; do not introduce another command framework unless asked.
- Do UI wiring in XAML bindings rather than code-behind.
- Implement `INotifyPropertyChanged` manually with `[CallerMemberName]`; do not add a base class or
  source generator without discussion.
- Use ViewModel events for view interaction and error reporting instead of direct view calls.

## Coding Conventions

- Namespaces follow folder structure: `NetImage`, `NetImage.Models`, `NetImage.ViewModels`, and so
  on.
- Use `var` when the type is obvious from the right-hand side.
- Prefer `Span<byte>` and `ReadOnlySpan<byte>` when parsing image data.
- Keep nullable annotations correct.
- Rely on implicit usings where possible.
- Avoid adding NuGet dependencies unless there is a clear need.

## Build And Test

```powershell
dotnet build NetImage.csproj
dotnet run --project NetImage.csproj
dotnet test NetImage.Tests/NetImage.Tests.csproj
```

The app requires the .NET 10 SDK and runs on Windows only.

## Product Constraints

- FAT12 and FAT16 are supported; FAT32 is not.
- Long file names are skipped.
- File and folder names are normalized to FAT 8.3 format.
- Cluster allocation uses a simple linear search.
- There is no undo/redo support.
- User input is not fully validated for FAT-illegal characters.
- Most file operations still run on the UI thread except `OpenAsync` and `SaveAsync`.

## Testing Notes

- Prefer direct tests for `DiskImageWorker` over UI integration tests.
- Test assets live under `NetImage.Tests/data/`.
- Temporary-file tests should use a unique directory under `Path.GetTempPath()` and clean up after
  themselves.
