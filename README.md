# NetImage

NetImage is a Windows desktop application for working with classic FAT disk images without dropping
to a command line or digging through a hex editor. You can open existing `.ima` and `.img` files,
create new blank images, browse their contents, extract files, make changes, and save the result
back to disk.

It is built with WPF and .NET 10, focuses on FAT12 and FAT16 images, and is designed to make
everyday image management feel quick and approachable.

## Why NetImage is useful

- Open existing `.ima` and `.img` disk images in a native Windows UI
- Create new blank FAT disk images from common preset sizes
- Browse folders and files with a tree view and a detail list
- Extract individual files or whole directories to your host machine
- Add files and entire folders back into the image
- Create folders directly inside the disk image
- Edit text files directly inside the image
- Delete files and folders and save the modified image back to disk
- Save in place or use Save As to write a new image file
- See total and free space for the loaded image in the status bar
- Keep track of image contents without relying on vintage tools or manual filesystem inspection

## What makes it nice

NetImage is intentionally focused. It does not try to be a giant emulator frontend or a general-purpose archive manager. It is a small, purpose-built tool for one job: making FAT disk images easy to work with.

That means:

- The workflow is simple and visual
- The codebase is lightweight and understandable
- The app is fast to launch and straightforward to use
- Common disk-image tasks are a few clicks away instead of a multi-step tooling process

## Supported formats

- FAT12
- FAT16
- Disk image files with `.ima` and `.img` extensions
- Raw FAT filesystems and MBR-partitioned disk images with a FAT12/FAT16 partition

## Current limitations

NetImage is already very capable for classic DOS-era image workflows, but a few limitations are worth knowing up front:

- FAT32 is not supported yet
- Long file names (LFN) are currently skipped
- File and folder names are normalized to FAT 8.3 format
- Cluster allocation uses a simple linear search
- There is currently no undo/redo system
- Text editing is intended for text-like files; known binary file types are not opened in the editor
- Most file operations still run on the UI thread except opening and saving

## Download

The latest tagged release is [v1.2](https://github.com/svpetry/NetImage/releases/tag/v1.2).

Build from source if you want the latest in-progress changes from the `main` branch.

## Building from source

Requirements:

- Windows
- .NET 10 SDK

Build the app:

```powershell
dotnet build NetImage.csproj
```

Run the app:

```powershell
dotnet run --project NetImage.csproj
```

Run the tests:

```powershell
dotnet test NetImage.Tests/NetImage.Tests.csproj
```

## Creating a release package

Maintainers can build the Windows release folder and an Explorer-friendly ZIP archive with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-Release.ps1
```

The script reads the version from `NetImage.csproj`, publishes the app, creates a ZIP in `artifacts\release\`, and prints the SHA-256 hash. To replace the asset of an existing GitHub release tag, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-Release.ps1 -UploadToGitHubRelease
```

## Project structure

- `Models/` contains the tree/list item model used by the UI
- `ViewModels/` contains the MVVM logic for commands, state, and application behavior
- `Views/` contains the WPF windows and dialogs
- `Workers/` contains the FAT filesystem parser and image modification logic
- `NetImage.Tests/` contains NUnit tests for the disk-image worker

## Typical workflow

1. Open an existing FAT disk image or create a new blank one.
2. Browse its folders and files in the tree and list panes.
3. Extract what you need, or add, create, delete, and edit content.
4. Keep an eye on remaining free space in the status bar.
5. Save the modified image back to disk or use Save As for a new file.

## Tech stack

- C# 13
- .NET 10
- WPF
- MVVM
- NUnit

## Status

NetImage is actively evolving. The tagged `v1.2` release covers the current browsing, modification,
blank-image creation, and text-editing workflow, and the `main` branch may include newer work in
progress.
