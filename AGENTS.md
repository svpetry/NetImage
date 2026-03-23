# AGENTS.md — NetImage Project Guide

This file provides a complete reference for coding agents working on the **NetImage** project.
Always read this file before making changes.

---

## Project Overview

**NetImage** is a Windows desktop application (WPF) written in **C# / .NET 10** that allows users to
open FAT-formatted floppy/hard-disk image files (`.ima`, `.img`) and browse/modify their contents.
The application supports viewing files in a tree/list view, adding files and folders, creating
folders, deleting entries, extracting files, and saving modified images.

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
| Testing | NUnit 4.3.2 with NUnit3TestAdapter |

---

## Repository Layout

```
NetImage/
├── App.xaml / App.xaml.cs           # WPF application entry point
├── AssemblyInfo.cs                   # Assembly metadata (themes)
├── NetImage.csproj                   # Main project file (excludes Tests)
├── NetImage.slnx                     # Solution file
├── diskette.png                      # Application icon
│
├── Models/
│   └── TreeItem.cs                   # INotifyPropertyChanged tree node model
│
├── ViewModels/
│   ├── MainViewModel.cs              # Main window view model; orchestrates commands & state
│   └── CreateFolderRequestEventArgs.cs # EventArgs for folder creation dialog
│
├── Views/
│   ├── MainView.xaml / MainView.xaml.cs      # Main window (toolbar, tree, list, status bar)
│   └── FolderNameDialog.xaml / .cs           # Dialog for creating new folders
│
├── Utils/
│   └── ActionCommand.cs              # ICommand implementation backed by Action<object?>
│
├── Workers/
│   └── DiskImageWorker.cs            # FAT filesystem parser and modifier
│
└── NetImage.Tests/
    ├── NetImage.Tests.csproj         # Test project (references main project)
    ├── Workers/
    │   └── DiskImageWorkerTests.cs   # NUnit tests for DiskImageWorker
    └── data/
        └── test.ima                  # Test disk image file
```

---

## Key Classes

### `Models/TreeItem.cs`
- A self-referential tree node used as items in the WPF `TreeView` and `ListView`.
- Properties:
  - `Name` (read-only `string`) — display name
  - `Path` (read-only `string`) — full backslash-separated path inside image (empty = root)
  - `Size` (read-only `long?`) — file size; `null` indicates a folder
  - `Modified` (read-only `DateTime?`) — last modified timestamp
  - `FormattedSize` — human-readable size string (e.g., "1.5 KB")
  - `FormattedModified` — formatted date string (e.g., "2024-01-15 14:30")
  - `IsFolder` — computed property (`Size == null`)
  - `IconGlyph` — Segoe Fluent Icons glyph for folder/file
  - `Children` (`ObservableCollection<TreeItem>`) — for hierarchical tree view
  - `Items` (`ObservableCollection<TreeItem>`) — for flat list view
  - `IsExpanded` / `IsSelected` — notifying properties for UI state
- Implements `INotifyPropertyChanged`.

### `ViewModels/MainViewModel.cs`
- Implements `INotifyPropertyChanged`.
- Commands: `OpenCommand`, `SaveCommand`, `SaveAsCommand`, `CloseCommand`, `CreateFolderCommand`, `AddFolderCommand`, `AddCommand`, `DeleteCommand`, `ExtractCommand`.
- Properties:
  - `TreeItems` (`ObservableCollection<TreeItem>`) — root collection for tree view
  - `CurrentFolder` (`TreeItem?`) — currently selected folder, drives list view
  - `SelectedItem` (`TreeItem?`) — currently selected item (file or folder)
  - `StatusText` — status bar text
- Events for UI communication: `CreateFolderRequested`, `CreateFolderError`, `AddError`, `DeleteError`, `ExtractError`, `SaveError`.
- `BuildTreeView()` constructs the full tree hierarchy from `FilesAndFolders`, including subdirectories.
- `GetSelectedFolderPath()` determines target folder for add operations based on selection.

### `Views/MainView.xaml`
- Three-row `Grid`: toolbar (row 0), content area (row 1), status bar (row 2).
- Content area has two panes:
  - Left: `TreeView` showing folder hierarchy
  - Right: `ListView` showing contents of selected folder (Name, Date modified, Size columns)
- Toolbar buttons with Segoe Fluent Icons for all operations.
- `DataContext` set declaratively to `<vm:MainViewModel/>`.

### `Views/FolderNameDialog.xaml`
- Simple dialog for creating new folders.
- Contains a `TextBox` for folder name (max 8 characters for FAT 8.3 format).
- Returns `FolderName` property when OK is clicked.

### `Utils/ActionCommand.cs`
- Thin `ICommand` wrapper around `Action<object?>`.
- `Enabled` property raises `CanExecuteChanged` when toggled.

### `Workers/DiskImageWorker.cs`
- Core FAT filesystem parser and modifier.
- `FileEntry` record: `(string Path, long? Size, DateTime? Modified)`
- Properties:
  - `FilePath` — path to the image file on disk
  - `VolumeLabel` — extracted volume label from FAT
  - `FilesAndFolders` — list of all entries found in the image
  - `IsLoaded` — whether the image has been opened
- Events: `LoadingStarted`, `LoadingCompleted`
- **Reading:**
  - `OpenAsync()` — loads image into memory and parses FAT filesystem
  - `GetFileContent(path)` — reads file bytes from the image
  - `ExtractFolder(sourcePath, destPath)` — extracts entire folder to host filesystem
- **Writing:**
  - `SaveAsync(path)` — saves modified image to disk
  - `AddFile(targetDirectory, fileName, content)` — adds a file to the image
  - `AddHostDirectory(targetDirectory, hostFolderPath)` — recursively adds a host folder
  - `CreateFolder(targetDirectory, folderName)` — creates a new folder
  - `DeleteEntry(path)` — deletes a file or folder (recursively for folders)
  - `GetFreeBytes()` — estimates available space on the image
- **FAT support:** FAT12 and FAT16; handles subdirectory traversal via cluster chains.

---

## MVVM Conventions

- **ViewModels** never directly reference `System.Windows` or WPF types; keep UI framework
  dependencies inside Views only.
- **Commands** are `ActionCommand` instances exposed as public properties on the ViewModel. Do not
  use `RelayCommand` or any third-party MVVM library unless explicitly introduced.
- **Data binding** is always done in XAML; code-behind files are kept minimal (no business logic).
- **INotifyPropertyChanged** is implemented manually with `[CallerMemberName]` — do not introduce
  a base class or source generator without discussing first.
- **UI-to-ViewModel communication** uses events on the ViewModel (e.g., `CreateFolderRequested`,
  `AddError`) rather than direct method calls from code-behind.

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
