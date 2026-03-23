# AGENTS.md — NetImage Project Guide

This file provides a complete reference for coding agents working on the **NetImage** project.
Always read this file before making changes.

---

## Project Overview

**NetImage** is a Windows desktop application (WPF) written in **C# / .NET 10** that allows users to
open FAT-formatted floppy/hard-disk image files (`.ima`, `.img`) and browse their contents in a
tree view. The application is in an early stage: the FAT filesystem parser is functional but the
tree-building and file-extraction features are still being developed.

---

## Technology Stack

| Concern | Choice |
|---|---|
| Language | C# 13, nullable enabled, implicit usings enabled |
| Framework | .NET 10 (`net10.0-windows`) |
| UI | WPF (Windows Presentation Foundation) |
| Architecture | MVVM (Model-View-ViewModel) |
| Build system | MSBuild via `NetImage.csproj` / `NetImage.slnx` |
| Output type | Windows executable (`WinExe`) |

---

## Repository Layout

```
NetImage/
├── App.xaml / App.xaml.cs       # WPF application entry point
├── AssemblyInfo.cs               # Assembly metadata
├── NetImage.csproj               # Single-project SDK-style project file
├── NetImage.slnx                 # Solution file
│
├── Models/
│   └── TreeItem.cs               # INotifyPropertyChanged tree node model
│
├── ViewModels/
│   └── MainViewModel.cs          # Main window view model; orchestrates commands & state
│
├── Views/
│   ├── MainView.xaml             # Main window XAML (menu, tree view, status bar)
│   └── MainView.xaml.cs         # Code-behind (minimal — only wires DataContext)
│
├── Utils/
│   └── ActionCommand.cs          # ICommand implementation backed by an Action<object?>
│
└── Workers/
    └── DiskImageWorker.cs        # Reads & parses FAT disk image files
```

---

## Key Classes

### `Models/TreeItem.cs`
- A self-referential tree node used as items in the WPF `TreeView`.
- Properties: `Name` (read-only `string`), `Children` (`ObservableCollection<TreeItem>`), `IsExpanded` (notifying `bool`).
- Implements `INotifyPropertyChanged`.

### `ViewModels/MainViewModel.cs`
- Implements `INotifyPropertyChanged`.
- Exposes three `ActionCommand` properties: `OpenCommand`, `CloseCommand`, `AddCommand`.
- Owns the `ObservableCollection<TreeItem> TreeItems` that drives the tree view.
- `StatusText` property is shown in the status bar.
- `ExecuteOpen` opens a file-picker dialog filtered to `*.ima;*.img`, creates a `DiskImageWorker`, calls `Open()`, then calls `BuildTreeView()`.
- `BuildTreeView()` constructs root-level `TreeItem` nodes from the flat `FilesAndFolders` list. **Note:** subdirectory recursion is not yet implemented.
- `ExecuteAdd` is a stub — currently only updates `StatusText`.

### `Views/MainView.xaml`
- Three-row `Grid`: menu bar (row 0), `TreeView` (row 1), `StatusBar` (row 2).
- `DataContext` is set declaratively in XAML to `<vm:MainViewModel/>`.
- Menu items bind directly to `OpenCommand`, `CloseCommand`, `AddCommand`.
- `TreeView` uses a `HierarchicalDataTemplate` that binds `Children` and `Name`.

### `Utils/ActionCommand.cs`
- Thin `ICommand` wrapper around `Action<object?>`.
- `Enabled` property raises `CanExecuteChanged` when toggled; used to enable/disable menu items.

### `Workers/DiskImageWorker.cs`
- Reads the entire image file into `byte[]` on `Open()`.
- `ParseFatFilesystem()` — validates the boot-sector signature (`0x55 0xAA` at offset 510–511), parses the BPB (BIOS Parameter Block), and calls `ReadDirectoryEntries`.
- `ReadDirectoryEntries()` iterates 32-byte directory entries, skipping deleted/empty ones, and appends decoded 8.3 filenames to the `FilesAndFolders` list.
- **Limitations:** only reads the root directory (no subdirectory traversal), no FAT12/FAT16/FAT32 type detection, no LFN (Long File Name) support.

---

## MVVM Conventions

- **ViewModels** never directly reference `System.Windows` or WPF types; keep UI framework
  dependencies inside Views only.
- **Commands** are `ActionCommand` instances exposed as public properties on the ViewModel. Do not
  use `RelayCommand` or any third-party MVVM library unless explicitly introduced.
- **Data binding** is always done in XAML; code-behind files are kept minimal (no business logic).
- **INotifyPropertyChanged** is implemented manually with `[CallerMemberName]` — do not introduce
  a base class or source generator without discussing first.

---

## Coding Style

- **Namespaces** follow the folder structure: `NetImage`, `NetImage.Models`, `NetImage.ViewModels`,
  `NetImage.Views`, `NetImage.Utils`, `NetImage.Workers`.
- Use `var` where the type is obvious from the right-hand side.
- Prefer `ReadOnlySpan<byte>` / `Span<byte>` over byte-array copies when parsing binary data.
- Nullable reference types are enabled project-wide; annotate accordingly.
- Implicit usings are enabled — do not add `using System;` or other BCL namespaces that are
  already in scope implicitly.
- No third-party NuGet packages are currently used; add only when necessary and keep the
  dependency footprint minimal.

---

## Building & Running

```powershell
# Build
dotnet build NetImage.csproj

# Run
dotnet run --project NetImage.csproj

# Or open NetImage.slnx in Visual Studio 2022 / JetBrains Rider and press F5
```

The project requires the **.NET 10 SDK** and runs on **Windows only** (WPF is Windows-exclusive).

---

## Known Limitations / Future Work

- `BuildTreeView()` only creates top-level nodes; subdirectory entries from `FilesAndFolders` are
  not yet recursively placed under their parent nodes.
- `DiskImageWorker` does not distinguish FAT12 / FAT16 / FAT32 variants.
- LFN directory entries are silently skipped.
- `ExecuteAdd` is a placeholder and does not yet inject files into the image.
- No unit or integration tests exist yet.
