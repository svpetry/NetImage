using Microsoft.Win32;
using NetImage.Models;
using NetImage.Utils;
using NetImage.Views;
using NetImage.Workers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NetImage.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private const string ApplicationName = "NetImage";
        private const string RepositoryUrl = "https://github.com/svpetry/NetImage";
        private string _statusText = "Ready";
        private string _diskSpaceText = string.Empty;
        private string _filesystemTypeText = string.Empty;
        private DiskImageWorker? _imageWorker;
        private TreeItem? _currentFolder;
        private TreeItem? _selectedItem;
        private readonly ObservableCollection<TreeItem> _selectedItems = new();
        private bool _canSaveCurrentImage;
        private bool _hasUnsavedChanges;
        private bool _isBusy;
        private bool _isProgressVisible;
        private bool _isProgressIndeterminate;
        private double _progressValue;
        private string _progressText = string.Empty;

        public event EventHandler<string>? AddError;

        public event EventHandler<CreateFolderRequestEventArgs>? CreateFolderRequested;
        public event EventHandler<AboutRequestEventArgs>? AboutRequested;
        public event EventHandler<string>? CreateFolderError;
        public event EventHandler<string>? DeleteError;

        public event EventHandler<string>? ExtractError;
        public event EventHandler<FormatRequestEventArgs>? FormatRequested;

        public event EventHandler<string>? SaveError;
        public event EventHandler<CloseImageRequestEventArgs>? CloseImageRequested;

        public event EventHandler<RenameRequestEventArgs>? RenameRequested;
        public event EventHandler<string>? RenameError;
        public event EventHandler<string>? MoveError;
        public event EventHandler? TreeViewBuilt;

        public MainViewModel()
        {
            AboutCommand = new ActionCommand(ExecuteAbout);
            NewCommand = new ActionCommand(ExecuteNew);
            OpenCommand = new ActionCommand(ExecuteOpen);
            CloseCommand = new ActionCommand(ExecuteClose) { Enabled = false };
            AddCommand = new ActionCommand(ExecuteAdd) { Enabled = false };
            AddFolderCommand = new ActionCommand(ExecuteAddFolder) { Enabled = false };
            CreateFolderCommand = new ActionCommand(ExecuteCreateFolder) { Enabled = false };
            DeleteCommand = new ActionCommand(ExecuteDelete) { Enabled = false };
            ExtractCommand = new ActionCommand(ExecuteExtract) { Enabled = false };
            FormatCommand = new ActionCommand(ExecuteFormat) { Enabled = false };
            EditCommand = new ActionCommand(ExecuteEdit) { Enabled = false };
            RenameCommand = new ActionCommand(ExecuteRename) { Enabled = false };
            BootSectorCommand = new ActionCommand(ExecuteBootSector) { Enabled = false };
            PartitionsCommand = new ActionCommand(ExecutePartitions) { Enabled = false };
            ImageMapCommand = new ActionCommand(ExecuteImageMap) { Enabled = false };
            SaveCommand = new ActionCommand(ExecuteSave) { Enabled = false };
            SaveAsCommand = new ActionCommand(ExecuteSaveAs) { Enabled = false };
            TreeItems = new ObservableCollection<TreeItem>();

            System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public ActionCommand AboutCommand { get; }
        public ActionCommand NewCommand { get; }
        public ActionCommand OpenCommand { get; }
        public ActionCommand CloseCommand { get; }
        public ActionCommand AddCommand { get; }
        public ActionCommand AddFolderCommand { get; }
        public ActionCommand CreateFolderCommand { get; }
        public ActionCommand DeleteCommand { get; }
        public ActionCommand ExtractCommand { get; }
        public ActionCommand FormatCommand { get; }
        public ActionCommand EditCommand { get; }
        public ActionCommand RenameCommand { get; }
        public ActionCommand BootSectorCommand { get; }
        public ActionCommand PartitionsCommand { get; }
        public ActionCommand ImageMapCommand { get; }
        public ActionCommand SaveCommand { get; }
        public ActionCommand SaveAsCommand { get; }
        public string ApplicationVersion => GetApplicationVersion();
        public string WindowTitle => $"{ApplicationName} {ApplicationVersion}";

        public string StatusText
        {
            get => _statusText;
            set
            {
                _statusText = value;
                OnPropertyChanged();
            }
        }

        public string DiskSpaceText
        {
            get => _diskSpaceText;
            private set
            {
                _diskSpaceText = value;
                OnPropertyChanged();
            }
        }

        public string FilesystemTypeText
        {
            get => _filesystemTypeText;
            private set
            {
                _filesystemTypeText = value;
                OnPropertyChanged();
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (_isBusy == value)
                    return;

                _isBusy = value;
                OnPropertyChanged();
            }
        }

        public bool IsProgressVisible
        {
            get => _isProgressVisible;
            private set
            {
                if (_isProgressVisible == value)
                    return;

                _isProgressVisible = value;
                OnPropertyChanged();
            }
        }

        public bool IsProgressIndeterminate
        {
            get => _isProgressIndeterminate;
            private set
            {
                if (_isProgressIndeterminate == value)
                    return;

                _isProgressIndeterminate = value;
                OnPropertyChanged();
            }
        }

        public double ProgressValue
        {
            get => _progressValue;
            private set
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    value = 0;
                }

                if (Math.Abs(_progressValue - value) < double.Epsilon)
                    return;

                _progressValue = value;
                OnPropertyChanged();
            }
        }

        public string ProgressText
        {
            get => _progressText;
            private set
            {
                if (string.Equals(_progressText, value, StringComparison.Ordinal))
                    return;

                _progressText = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<TreeItem> TreeItems { get; }

        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            private set
            {
                _hasUnsavedChanges = value;
                OnPropertyChanged();
            }
        }

        public TreeItem? CurrentFolder
        {
            get => _currentFolder;
            set
            {
                _currentFolder = value;
                OnPropertyChanged();
            }
        }

        /// <summary>The currently selected node in the tree view, set from code-behind.</summary>
        public TreeItem? SelectedItem
        {
            get => _selectedItem;
            set
            {
                _selectedItem = value;
                OnPropertyChanged();
                UpdateCommandStates();
            }
        }

        /// <summary>Collection of selected items in the ListView (supports multiselect).</summary>
        public ObservableCollection<TreeItem> SelectedItems => _selectedItems;

        /// <summary>
        /// Returns true if the file has a known binary extension.
        /// Based on common DOS/Windows binary file types from the DOS era and beyond.
        /// </summary>
        private static bool IsBinaryFile(string fileName)
        {
            var extension = System.IO.Path.GetExtension(fileName).ToUpperInvariant();
            return BinaryExtensions.Contains(extension);
        }

        // Known binary file extensions (DOS era and common Windows formats)
        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            // Executables
            ".COM", ".EXE", ".PIF", ".SCR", ".DRV", ".DLL",
            // Archives
            ".ZIP", ".ARC", ".LZH", ".LHA", ".ARJ", ".CAB", ".TAR", ".GZ", ".BZ2",
            // Disk images
            ".IMG", ".IMA", ".ISO", ".BIN", ".FDI", ".DD",
            // Compiled code / object files
            ".OBJ", ".O", ".LIB", ".A",
            // Bitmapped images
            ".BMP", ".GIF", ".JPG", ".JPEG", ".PNG", ".TIF", ".TIFF", ".ICO", ".CUR", ".PCX", ".TGA",
            // Audio/video
            ".WAV", ".MP3", ".WMA", ".AVI", ".MPG", ".MPEG", ".MOV", ".RM", ".ASF",
            // Fonts
            ".FON", ".TTF", ".OTF", ".FNT",
            // Database / data files
            ".DBF", ".MDB", ".ACCDB",
            // Microsoft Office (pre-2007 binary formats)
            ".XLS", ".PPT",
            // Compiled HTML
            ".CHM",
            // Cabinet / compressed
            ".CAB", ".Z",
            // Other binary
            ".PRN", ".PS", ".PCL", ".EOT", ".CAB"
        };

        private async void ExecuteNew(object? parameter)
        {
            if (_imageWorker != null && _hasUnsavedChanges)
            {
                var args = new CloseImageRequestEventArgs();
                CloseImageRequested?.Invoke(this, args);

                if (!args.AllowClose)
                    return;

                if (args.SaveChanges && !string.IsNullOrEmpty(_imageWorker.FilePath))
                {
                    await RunBusyOperationAsync(
                        "Saving image...",
                        () => Task.Run(async () => await _imageWorker.SaveAsync(_imageWorker.FilePath)),
                        progressText: System.IO.Path.GetFileName(_imageWorker.FilePath));
                    HasUnsavedChanges = false;
                    StatusText = $"Saved: {_imageWorker.FilePath}";
                }
            }

            var dialog = new NewDiskImageDialog();
            if (dialog.ShowDialog() != true || dialog.SelectedSize == 0)
                return;

            // Create new image worker with blank FAT image
            _imageWorker = new DiskImageWorker(string.Empty);

            if (dialog.IsHardDisk)
            {
                _imageWorker.CreateHardDiskImage(dialog.Cylinders, dialog.Heads, dialog.SectorsPerTrack, "DISK");
            }
            else
            {
                _imageWorker.CreateBlankImage(dialog.SelectedSize, "DISK");
            }

            _canSaveCurrentImage = false;
            _hasUnsavedChanges = false;

            BuildTreeView();
            RefreshDiskSpaceText();
            UpdateCommandStates();

            var sizeMB = dialog.SelectedSize / (1024.0 * 1024.0);
            StatusText = dialog.IsHardDisk
                ? $"New {sizeMB:F2} MB hard disk image created ({dialog.Cylinders}x{dialog.Heads}x{dialog.SectorsPerTrack})"
                : $"New {dialog.SelectedSize / 1024} KB floppy disk image created";
        }

        private void ExecuteAbout(object? parameter)
        {
            AboutRequested?.Invoke(this, new AboutRequestEventArgs(ApplicationName, ApplicationVersion, RepositoryUrl));
        }

        private async void ExecuteOpen(object? parameter)
        {
            if (_imageWorker != null && _hasUnsavedChanges)
            {
                var args = new CloseImageRequestEventArgs();
                CloseImageRequested?.Invoke(this, args);

                if (!args.AllowClose)
                    return;

                if (args.SaveChanges && !string.IsNullOrEmpty(_imageWorker.FilePath))
                {
                    await RunBusyOperationAsync(
                        "Saving image...",
                        () => Task.Run(async () => await _imageWorker.SaveAsync(_imageWorker.FilePath)),
                        progressText: System.IO.Path.GetFileName(_imageWorker.FilePath));
                    HasUnsavedChanges = false;
                    StatusText = $"Saved: {_imageWorker.FilePath}";
                }
            }

            var openFileDialog = new OpenFileDialog
            {
                Filter = "Disk Image Files|*.ima;*.img|All Files|*.*"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                await OpenImageFileAsync(openFileDialog.FileName);
            }
        }

        /// <summary>
        /// Opens an image file by path. Used for command-line arguments and file associations.
        /// </summary>
        public async Task OpenImageFileAsync(string filePath)
        {
            var worker = new DiskImageWorker(filePath);
            try
            {
                await RunBusyOperationAsync(
                    "Opening image...",
                    () => Task.Run(async () => await worker.OpenAsync()),
                    progressText: System.IO.Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                StatusText = $"Could not open image: {ex.Message}";
                return;
            }

            if (!worker.IsLoaded)
            {
                StatusText = worker.FilesystemType == DiskImageWorker.FatType.Fat32
                    ? "FAT32 images are not supported"
                    : "Image is not formatted or has an unsupported format";
                return;
            }

            _imageWorker = worker;
            _canSaveCurrentImage = true;
            BuildTreeView();
            RefreshDiskSpaceText();
            UpdateCommandStates();
            StatusText = $"Opened: {filePath}";
        }

        private void BuildTreeView()
        {
            var currentFolderPath = GetFolderPathToRestore();

            TreeItems.Clear();
            ClearSelectedItems();
            CurrentFolder = null;
            SelectedItem = null;

            var rootNode = new TreeItem(_imageWorker?.VolumeLabel ?? string.Empty, string.Empty);
            var nodeByPath = new Dictionary<string, TreeItem>(StringComparer.OrdinalIgnoreCase);
            nodeByPath[""] = rootNode;

            if (_imageWorker == null || _imageWorker.FilesAndFolders.Count == 0)
            {
                rootNode.IsExpanded = true;
                TreeItems.Add(rootNode);
                CurrentFolder = rootNode;
                SelectedItem = rootNode;
                rootNode.IsSelected = true;
                TreeViewBuilt?.Invoke(this, EventArgs.Empty);
                return;
            }

            foreach (var entry in _imageWorker.FilesAndFolders)
            {
                var parts = entry.Path.Split('\\');
                TreeItem currentParent = rootNode;
                var pathSoFar = string.Empty;

                for (int i = 0; i < parts.Length; i++)
                {
                    var part = parts[i];
                    var isLeaf = i == parts.Length - 1;
                    var nodePath = i == 0 ? part : $"{pathSoFar}\\{part}";

                    if (!nodeByPath.TryGetValue(nodePath, out var node))
                    {
                        node = new TreeItem(part, nodePath, isLeaf ? entry.Size : null, isLeaf ? entry.Modified : null);
                        nodeByPath[nodePath] = node;

                        if (node.IsFolder)
                        {
                            currentParent.Children.Add(node);
                        }
                        currentParent.Items.Add(node);
                    }

                    currentParent = node;
                    pathSoFar = nodePath;
                }
            }

            rootNode.IsExpanded = true;
            TreeItems.Add(rootNode);

            var restoredFolder = ResolveFolderToRestore(nodeByPath, currentFolderPath);
            ExpandFolderPath(nodeByPath, restoredFolder.Path);
            CurrentFolder = restoredFolder;
            SelectedItem = restoredFolder;
            restoredFolder.IsSelected = true;
            restoredFolder.IsExpanded = true;

            // Notify the view that the tree has been rebuilt
            TreeViewBuilt?.Invoke(this, EventArgs.Empty);
        }

        private string? GetFolderPathToRestore()
        {
            if (_currentFolder != null)
                return _currentFolder.Path;

            if (_selectedItem == null)
                return null;

            return _selectedItem.IsFolder ? _selectedItem.Path : GetParentFolderPath(_selectedItem.Path);
        }

        private static TreeItem ResolveFolderToRestore(IReadOnlyDictionary<string, TreeItem> nodeByPath, string? folderPath)
        {
            var candidatePath = folderPath;
            while (candidatePath != null)
            {
                if (nodeByPath.TryGetValue(candidatePath, out var folder))
                    return folder;

                candidatePath = GetParentFolderPath(candidatePath);
            }

            return nodeByPath[string.Empty];
        }

        private static void ExpandFolderPath(IReadOnlyDictionary<string, TreeItem> nodeByPath, string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                return;

            var pathParts = folderPath.Split('\\');
            var pathSoFar = string.Empty;
            foreach (var part in pathParts)
            {
                pathSoFar = string.IsNullOrEmpty(pathSoFar) ? part : $"{pathSoFar}\\{part}";
                if (nodeByPath.TryGetValue(pathSoFar, out var folder))
                {
                    folder.IsExpanded = true;
                }
            }
        }

        private void ClearSelectedItems()
        {
            while (_selectedItems.Count > 0)
            {
                _selectedItems.RemoveAt(0);
            }
        }

        private async void ExecuteClose(object? parameter)
        {
            if (_imageWorker == null)
                return;

            if (_hasUnsavedChanges)
            {
                var args = new CloseImageRequestEventArgs();
                CloseImageRequested?.Invoke(this, args);

                if (!args.AllowClose)
                    return;

                if (args.SaveChanges && !string.IsNullOrEmpty(_imageWorker.FilePath))
                {
                    await RunBusyOperationAsync(
                        "Saving image...",
                        () => Task.Run(async () => await _imageWorker.SaveAsync(_imageWorker.FilePath)),
                        progressText: System.IO.Path.GetFileName(_imageWorker.FilePath));
                    HasUnsavedChanges = false;
                    StatusText = $"Saved: {_imageWorker.FilePath}";
                }
            }

            _imageWorker = null;
            _canSaveCurrentImage = false;
            _hasUnsavedChanges = false;
            TreeItems.Clear();
            CurrentFolder = null;
            SelectedItem = null;
            ClearDiskSpaceText();
            FilesystemTypeText = string.Empty;
            ResetProgress();
            UpdateCommandStates();
            StatusText = "Ready";
        }

        private async void ExecuteCreateFolder(object? parameter)
        {
            if (_imageWorker == null || IsBusy)
                return;

            var worker = _imageWorker;
            var args = new CreateFolderRequestEventArgs();
            CreateFolderRequested?.Invoke(this, args);

            if (string.IsNullOrWhiteSpace(args.FolderName))
                return;

            var targetDir = GetSelectedFolderPath();

            try
            {
                await RunBusyOperationAsync(
                    "Creating folder...",
                    () => Task.Run(() => worker.CreateFolder(targetDir, args.FolderName)),
                    progressText: args.FolderName);
            }
            catch (Exception ex)
            {
                CreateFolderError?.Invoke(this, $"Could not create folder:\n{ex.Message}");
                return;
            }

            BuildTreeView();
            RefreshDiskSpaceText();
            HasUnsavedChanges = true;

            var location = string.IsNullOrEmpty(targetDir) ? "root" : targetDir;
            StatusText = $"Created folder '{args.FolderName}' in {location}";
        }

        private async void ExecuteAdd(object? parameter)
        {
            if (_imageWorker == null || IsBusy)
                return;

            var worker = _imageWorker;
            var openFileDialog = new OpenFileDialog
            {
                Title = "Select file to add to disk image",
                Filter = "All Files|*.*"
            };

            if (openFileDialog.ShowDialog() != true)
                return;

            var hostPath = openFileDialog.FileName;
            var fileName = System.IO.Path.GetFileName(hostPath);
            var targetDir = GetSelectedFolderPath();

            try
            {
                await RunBusyOperationAsync(
                    "Adding file...",
                    async () =>
                    {
                        var content = await Task.Run(() => System.IO.File.ReadAllBytes(hostPath));
                        var freeBytes = worker.GetFreeBytes();
                        if (content.Length > freeBytes)
                        {
                            throw new InvalidOperationException(BuildNotEnoughSpaceMessage("File size", content.Length, freeBytes));
                        }

                        await Task.Run(() => worker.AddFile(targetDir, fileName, content));
                    },
                    progressText: fileName);
            }
            catch (Exception ex)
            {
                AddError?.Invoke(this, $"Could not add the selected file:\n{ex.Message}");
                return;
            }

            BuildTreeView();
            RefreshDiskSpaceText();
            HasUnsavedChanges = true;

            var location = string.IsNullOrEmpty(targetDir) ? "root" : targetDir;
            StatusText = $"Added '{fileName}' to {location}";
        }

        private async void ExecuteAddFolder(object? parameter)
        {
            if (_imageWorker == null || IsBusy)
                return;

            var worker = _imageWorker;
            var folderDialog = new OpenFolderDialog
            {
                Title = "Select folder to add to disk image"
            };

            if (folderDialog.ShowDialog() != true)
                return;

            var hostPath = folderDialog.FolderName;
            var folderName = new DirectoryInfo(hostPath).Name;

            var targetDir = GetSelectedFolderPath();

            try
            {
                await RunBusyOperationAsync(
                    "Scanning folder...",
                    async () =>
                    {
                        var totalSize = await Task.Run(() => CalculateDirectorySize(hostPath));
                        var freeBytes = worker.GetFreeBytes();
                        if (totalSize > freeBytes)
                        {
                            throw new InvalidOperationException(BuildNotEnoughSpaceMessage("Folder size", totalSize, freeBytes));
                        }

                        ApplyOperationProgress("Adding folder...", new OperationProgress(0, totalSize, folderName));
                        await worker.AddHostDirectoryAsync(
                            targetDir,
                            hostPath,
                            totalSize,
                            CreateProgressReporter("Adding folder..."));
                    },
                    progressText: folderName);
            }
            catch (Exception ex)
            {
                AddError?.Invoke(this, $"Could not add the selected folder:\n{ex.Message}");
                return;
            }

            BuildTreeView();
            RefreshDiskSpaceText();
            HasUnsavedChanges = true;

            var location = string.IsNullOrEmpty(targetDir) ? "root" : targetDir;
            StatusText = $"Added folder '{folderName}' to {location}";
        }

        private async void ExecuteDelete(object? parameter)
        {
            if (_imageWorker == null || IsBusy)
                return;

            // Get items to delete: use SelectedItems if multiselect, otherwise fall back to _selectedItem
            var itemsToDelete = _selectedItems.Count > 0
                ? _selectedItems.ToList()
                : (_selectedItem != null ? new List<TreeItem> { _selectedItem } : null);

            if (itemsToDelete == null || itemsToDelete.Count == 0)
                return;

            // Filter out the root node (empty path) - cannot be deleted
            var rootNode = itemsToDelete.FirstOrDefault(i => string.IsNullOrEmpty(i.Path));
            if (rootNode != null)
            {
                itemsToDelete.Remove(rootNode);
                if (itemsToDelete.Count == 0)
                    return;
            }

            var worker = _imageWorker;

            try
            {
                await RunBusyOperationAsync(
                    "Deleting...",
                    async () =>
                    {
                        foreach (var item in itemsToDelete)
                        {
                            worker.DeleteEntry(item.Path);
                        }
                    },
                    isIndeterminate: true,
                    progressText: itemsToDelete.Count > 1 ? $"{itemsToDelete.Count} items" : itemsToDelete[0].Name);
            }
            catch (Exception ex)
            {
                DeleteError?.Invoke(this, ex.Message);
                return;
            }

            BuildTreeView();
            RefreshDiskSpaceText();
            HasUnsavedChanges = true;
            StatusText = itemsToDelete.Count > 1
                ? $"Deleted {itemsToDelete.Count} items"
                : $"Deleted '{itemsToDelete[0].Name}'";
        }

        public async Task MoveItemsAsync(IEnumerable<TreeItem> itemsToMove, string targetDirectory)
        {
            if (_imageWorker == null || IsBusy)
                return;

            var worker = _imageWorker;
            var itemList = itemsToMove.ToList();

            // Filter out folders (cannot move folders for now)
            var filesToMove = itemList.Where(i => !i.IsFolder).ToList();

            // Filter out files already in the target directory
            filesToMove = filesToMove
                .Where(f => !GetParentFolderPath(f.Path).Equals(targetDirectory, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (filesToMove.Count == 0)
                return;

            try
            {
                await RunBusyOperationAsync(
                    "Moving...",
                    () => Task.Run(() =>
                    {
                        foreach (var item in filesToMove)
                        {
                            worker.MoveEntry(item.Path, targetDirectory);
                        }
                    }),
                    isIndeterminate: true,
                    progressText: filesToMove.Count > 1 ? $"{filesToMove.Count} files" : filesToMove[0].Name);
            }
            catch (Exception ex)
            {
                MoveError?.Invoke(this, ex.Message);
                return;
            }

            BuildTreeView();
            RefreshDiskSpaceText();
            HasUnsavedChanges = true;
            StatusText = filesToMove.Count > 1
                ? $"Moved {filesToMove.Count} files"
                : $"Moved '{filesToMove[0].Name}'";
        }

        private async void ExecuteExtract(object? parameter)
        {
            if (_imageWorker == null || IsBusy)
                return;

            // Get items to extract: use SelectedItems if multiselect, otherwise fall back to _selectedItem
            var itemsToExtract = _selectedItems.Count > 0
                ? _selectedItems.ToList()
                : (_selectedItem != null ? new List<TreeItem> { _selectedItem } : null);

            if (itemsToExtract == null || itemsToExtract.Count == 0)
                return;

            var worker = _imageWorker;

            // Determine extraction mode based on selection
            var hasFolders = itemsToExtract.Any(i => i.IsFolder);
            var hasFiles = itemsToExtract.Any(i => !i.IsFolder);
            var isSingleItem = itemsToExtract.Count == 1;

            // If mixed selection or multiple folders, require folder destination
            if (hasFolders && hasFiles || (hasFolders && itemsToExtract.Count > 1))
            {
                var folderDialog = new OpenFolderDialog
                {
                    Title = "Select Destination Folder"
                };

                if (folderDialog.ShowDialog() != true)
                    return;

                var destPath = folderDialog.FolderName;

                try
                {
                    await RunBusyOperationAsync(
                        "Extracting...",
                        async () =>
                        {
                            long totalBytes = 0;
                            foreach (var item in itemsToExtract)
                            {
                                if (item.IsFolder)
                                {
                                    var destFullPath = System.IO.Path.Combine(destPath, item.Name);
                                    var folderSize = CalculateFolderSize(item.Path);
                                    totalBytes += folderSize;
                                    await worker.ExtractFolderAsync(item.Path, destFullPath, CreateProgressReporter("Extracting..."));
                                }
                                else
                                {
                                    var content = worker.GetFileContent(item.Path);
                                    if (content != null)
                                    {
                                        var destFullPath = System.IO.Path.Combine(destPath, item.Name);
                                        System.IO.File.WriteAllBytes(destFullPath, content);
                                        totalBytes += content.Length;
                                    }
                                }
                            }
                        },
                        progressText: $"{itemsToExtract.Count} items");
                    StatusText = $"Extracted {itemsToExtract.Count} items to '{destPath}'";
                }
                catch (Exception ex)
                {
                    ExtractError?.Invoke(this, $"Failed to extract items:\n{ex.Message}");
                }
            }
            else if (hasFolders)
            {
                // Only folders selected
                var folderDialog = new OpenFolderDialog
                {
                    Title = "Select Destination Folder"
                };

                if (folderDialog.ShowDialog() != true)
                    return;

                try
                {
                    foreach (var item in itemsToExtract)
                    {
                        if (item.IsFolder)
                        {
                            var destPath = isSingleItem && string.IsNullOrEmpty(item.Path)
                                ? folderDialog.FolderName
                                : System.IO.Path.Combine(folderDialog.FolderName, item.Name);

                            await RunBusyOperationAsync(
                                "Extracting folder...",
                                () => worker.ExtractFolderAsync(item.Path, destPath, CreateProgressReporter("Extracting folder...")),
                                progressText: item.Name);
                        }
                    }

                    StatusText = itemsToExtract.Count > 1
                        ? $"Extracted {itemsToExtract.Count} folders to '{folderDialog.FolderName}'"
                        : $"Extracted folder '{itemsToExtract[0].Name}' to '{folderDialog.FolderName}'";
                }
                catch (Exception ex)
                {
                    ExtractError?.Invoke(this, $"Failed to extract folder(s):\n{ex.Message}");
                }
            }
            else
            {
                // Only files selected
                if (isSingleItem)
                {
                    // Single file: use SaveFileDialog for explicit path
                    var saveFileDialog = new SaveFileDialog
                    {
                        Title = "Extract File",
                        FileName = itemsToExtract[0].Name,
                        Filter = "All Files|*.*"
                    };

                    if (saveFileDialog.ShowDialog() == true)
                    {
                        try
                        {
                            await RunBusyOperationAsync(
                                "Extracting file...",
                                async () =>
                                {
                                    var item = itemsToExtract[0];
                                    var content = await Task.Run(() => worker.GetFileContent(item.Path));
                                    if (content == null)
                                    {
                                        throw new InvalidOperationException($"Could not extract '{item.Name}'. File not found or read error.");
                                    }

                                    await Task.Run(() => System.IO.File.WriteAllBytes(saveFileDialog.FileName, content));
                                },
                                progressText: itemsToExtract[0].Name);
                            StatusText = $"Extracted '{itemsToExtract[0].Name}' to '{saveFileDialog.FileName}'";
                        }
                        catch (Exception ex)
                        {
                            ExtractError?.Invoke(this, $"Failed to save extracted file:\n{ex.Message}");
                        }
                    }
                }
                else
                {
                    // Multiple files: require folder destination
                    var folderDialog = new OpenFolderDialog
                    {
                        Title = "Select Destination Folder"
                    };

                    if (folderDialog.ShowDialog() != true)
                        return;

                    var destPath = folderDialog.FolderName;

                    try
                    {
                        await RunBusyOperationAsync(
                            "Extracting files...",
                            async () =>
                            {
                                foreach (var item in itemsToExtract)
                                {
                                    var content = await Task.Run(() => worker.GetFileContent(item.Path));
                                    if (content != null)
                                    {
                                        var destFullPath = System.IO.Path.Combine(destPath, item.Name);
                                        await Task.Run(() => System.IO.File.WriteAllBytes(destFullPath, content));
                                    }
                                }
                            },
                            progressText: $"{itemsToExtract.Count} files");
                        StatusText = $"Extracted {itemsToExtract.Count} files to '{destPath}'";
                    }
                    catch (Exception ex)
                    {
                        ExtractError?.Invoke(this, $"Failed to extract files:\n{ex.Message}");
                    }
                }
            }
        }

        private async void ExecuteFormat(object? parameter)
        {
            if (_imageWorker == null || IsBusy)
                return;

            var args = new FormatRequestEventArgs();
            FormatRequested?.Invoke(this, args);

            if (!args.ConfirmFormat)
                return;

            var worker = _imageWorker;

            try
            {
                await RunBusyOperationAsync(
                    "Formatting image...",
                    () => Task.Run(() => worker.FormatImage()),
                    progressText: "Formatting");

                BuildTreeView();
                RefreshDiskSpaceText();
                HasUnsavedChanges = true;
                StatusText = "Disk image formatted successfully.";
            }
            catch (Exception ex)
            {
                StatusText = $"Could not format image: {ex.Message}";
            }
        }

        private async void ExecuteEdit(object? parameter)
        {
            if (_imageWorker == null || _selectedItem == null || IsBusy)
                return;

            var worker = _imageWorker;
            var item = _selectedItem;
            byte[]? content = null;

            try
            {
                await RunBusyOperationAsync(
                    "Loading file...",
                    async () =>
                    {
                        content = await Task.Run(() => worker.GetFileContent(item.Path));
                    },
                    progressText: item.Name);
            }
            catch (Exception ex)
            {
                StatusText = $"Could not read '{item.Name}': {ex.Message}";
                return;
            }

            if (content == null)
            {
                StatusText = $"Could not read '{item.Name}'. File not found or read error.";
                return;
            }

            // Open edit dialog
            var dialog = new FileEditDialog();
            dialog.Owner = System.Windows.Application.Current.MainWindow;
            dialog.SetFileContent(item.Name, content, item.Size, item.Modified);

            if (dialog.ShowDialog() != true || dialog.EditedContent == null)
                return;

            try
            {
                await RunBusyOperationAsync(
                    "Saving file changes...",
                    () => Task.Run(() => worker.UpdateFile(item.Path, dialog.EditedContent)),
                    progressText: item.Name);
                BuildTreeView();
                RefreshDiskSpaceText();
                HasUnsavedChanges = true;
                StatusText = $"Saved changes to '{item.Name}'";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to save file: {ex.Message}";
            }
        }

        private void ExecuteRename(object? parameter)
        {
            if (_imageWorker == null || _selectedItem == null || IsBusy)
                return;

            RenameRequested?.Invoke(this, new RenameRequestEventArgs(_selectedItem));
        }

        public void PerformRename(TreeItem item, string newName)
        {
            if (_imageWorker == null)
                return;

            try
            {
                // Check if renaming the root node (empty path) - this changes the volume label
                if (string.IsNullOrEmpty(item.Path))
                {
                    _imageWorker.SetVolumeLabel(newName);
                    StatusText = $"Volume label changed to '{newName}'";
                }
                else
                {
                    _imageWorker.RenameEntry(item.Path, newName);
                    StatusText = $"Renamed '{item.Name}' to '{newName}'";
                }
                BuildTreeView();
                RefreshDiskSpaceText();
                HasUnsavedChanges = true;
            }
            catch (Exception ex)
            {
                RenameError?.Invoke(this, $"Failed to rename: {ex.Message}");
            }
        }

        private async void ExecuteBootSector(object? parameter)
        {
            if (_imageWorker == null || IsBusy)
                return;

            var worker = _imageWorker;

            // Get the boot sector from the image data
            byte[] bootSector = await Task.Run(() =>
            {
                var imageData = worker.GetImageData();
                if (imageData == null || imageData.Length < 512)
                    throw new InvalidOperationException("Image is too small to contain a boot sector.");

                // Check if this is a partitioned image (MBR)
                long partitionStartSector = worker.GetPartitionStartSector();
                long bootSectorOffset = partitionStartSector * 512;

                if (bootSectorOffset + 512 > imageData.Length)
                    throw new InvalidOperationException("Boot sector is out of bounds.");

                var bs = new byte[512];
                Array.Copy(imageData, (int)bootSectorOffset, bs, 0, 512);
                return bs;
            });

            if (bootSector == null)
            {
                StatusText = "Could not read boot sector.";
                return;
            }

            // Open boot sector editor dialog
            var dialog = new BootSectorDialog();
            dialog.Owner = System.Windows.Application.Current.MainWindow;
            dialog.SetBootSector(bootSector);

            if (dialog.ShowDialog() != true || dialog.EditedBootSector == null)
                return;

            // Save the edited boot sector back to the image
            try
            {
                await RunBusyOperationAsync(
                    "Saving boot sector...",
                    () => Task.Run(() =>
                    {
                        long partitionStartSector = worker.GetPartitionStartSector();
                        long bootSectorOffset = partitionStartSector * 512;
                        worker.UpdateBootSector((int)bootSectorOffset, dialog.EditedBootSector);
                    }),
                    progressText: "Boot Sector");

                HasUnsavedChanges = true;
                StatusText = "Boot sector saved.";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to save boot sector: {ex.Message}";
            }
        }

        private void ExecuteImageMap(object? parameter)
        {
            if (_imageWorker == null || IsBusy)
                return;

            var dialog = new ImageMapDialog();
            dialog.Owner = System.Windows.Application.Current.MainWindow;
            dialog.LoadImage(_imageWorker);
            dialog.ShowDialog();
        }


        private async void ExecuteSave(object? parameter)
        {
            if (_imageWorker == null || IsBusy)
                return;

            var worker = _imageWorker;
            try
            {
                await RunBusyOperationAsync(
                    "Saving image...",
                    () => Task.Run(async () => await worker.SaveAsync(worker.FilePath)),
                    progressText: System.IO.Path.GetFileName(worker.FilePath));
                HasUnsavedChanges = false;
                StatusText = $"Saved: {worker.FilePath}";
            }
            catch (Exception ex)
            {
                SaveError?.Invoke(this, $"Could not save image:\n{ex.Message}");
            }
        }

        private void ExecutePartitions(object? parameter)
        {
            if (_imageWorker == null || _imageWorker.Partitions.Count == 0 || IsBusy)
                return;

            var dialog = new PartitionsDialog(_imageWorker.Partitions, _imageWorker.BytesPerSector);
            dialog.ShowDialog();
        }

        private async void ExecuteSaveAs(object? parameter)
        {
            if (_imageWorker == null || IsBusy)
                return;

            var worker = _imageWorker;
            var saveFileDialog = new SaveFileDialog
            {
                Title = "Save Disk Image As",
                Filter = "Disk Image Files|*.ima;*.img|All Files|*.*",
                FileName = string.IsNullOrEmpty(worker.FilePath) ? "newdisk.img" : System.IO.Path.GetFileName(worker.FilePath)
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    await RunBusyOperationAsync(
                        "Saving image...",
                        () => Task.Run(async () => await worker.SaveAsync(saveFileDialog.FileName)),
                        progressText: System.IO.Path.GetFileName(saveFileDialog.FileName));
                    worker.FilePath = saveFileDialog.FileName;
                    _canSaveCurrentImage = true;
                    HasUnsavedChanges = false;
                    UpdateCommandStates();
                    StatusText = $"Saved as: {saveFileDialog.FileName}";
                }
                catch (Exception ex)
                {
                    SaveError?.Invoke(this, $"Could not save image:\n{ex.Message}");
                }
            }
        }

        /// <summary>
        /// Returns the target folder path for a new file based on tree selection.
        /// - Nothing selected  → empty string (root)
        /// - Folder selected   → that folder's path
        /// - File selected     → parent folder's path
        /// </summary>
        private string GetSelectedFolderPath()
        {
            if (_selectedItem == null)
                return string.Empty;

            // A folder has no Size (Size == null)
            if (_selectedItem.IsFolder)
                return _selectedItem.Path;

            return GetParentFolderPath(_selectedItem.Path) ?? string.Empty;
        }

        private static string? GetParentFolderPath(string path)
        {
            var lastSlash = path.LastIndexOf('\\');
            return lastSlash >= 0 ? path[..lastSlash] : string.Empty;
        }

        private async Task RunBusyOperationAsync(string statusText, Func<Task> operation, bool isIndeterminate = true, string? progressText = null)
        {
            BeginBusy(statusText, isIndeterminate, progressText);
            try
            {
                await operation();
            }
            finally
            {
                EndBusy();
            }
        }

        private void BeginBusy(string statusText, bool isIndeterminate, string? progressText)
        {
            IsBusy = true;
            StatusText = statusText;
            IsProgressVisible = true;
            IsProgressIndeterminate = isIndeterminate;
            ProgressValue = 0;
            ProgressText = progressText ?? statusText;
            UpdateCommandStates();
        }

        private void EndBusy()
        {
            IsBusy = false;
            ResetProgress();
            UpdateCommandStates();
        }

        private void ResetProgress()
        {
            IsProgressVisible = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }

        private IProgress<OperationProgress> CreateProgressReporter(string statusText)
        {
            return new Progress<OperationProgress>(progress => ApplyOperationProgress(statusText, progress));
        }

        private void ApplyOperationProgress(string statusText, OperationProgress progress)
        {
            StatusText = statusText;
            IsProgressVisible = true;
            IsProgressIndeterminate = progress.TotalBytes <= 0;
            ProgressValue = progress.TotalBytes <= 0
                ? 0
                : Math.Clamp(progress.ProcessedBytes * 100.0 / progress.TotalBytes, 0, 100);

            var progressSummary = progress.TotalBytes <= 0
                ? "Working..."
                : $"{FormatBytes(progress.ProcessedBytes)} / {FormatBytes(progress.TotalBytes)}";
            ProgressText = string.IsNullOrWhiteSpace(progress.CurrentItem)
                ? progressSummary
                : $"{progressSummary} - {progress.CurrentItem}";
        }

        private void UpdateCommandStates()
        {
            var hasLoadedImage = _imageWorker != null && _imageWorker.IsLoaded;
            var hasWorker = _imageWorker != null;
            var hasSelection = _selectedItems.Count > 0;
            var hasTreeSelection = _selectedItem != null && _selectedItem.IsFolder;

            NewCommand.Enabled = !IsBusy;
            OpenCommand.Enabled = !IsBusy;
            CloseCommand.Enabled = hasWorker && !IsBusy;
            AddCommand.Enabled = hasLoadedImage && !IsBusy;
            AddFolderCommand.Enabled = hasLoadedImage && !IsBusy;
            CreateFolderCommand.Enabled = hasLoadedImage && !IsBusy;
            DeleteCommand.Enabled = hasLoadedImage && (hasSelection || hasTreeSelection) && !IsBusy;
            ExtractCommand.Enabled = hasLoadedImage && (hasSelection || hasTreeSelection) && !IsBusy;
            FormatCommand.Enabled = hasLoadedImage && !IsBusy;
            EditCommand.Enabled = hasLoadedImage &&
                                  _selectedItem != null &&
                                  _selectedItems.Count == 1 &&
                                  !_selectedItem.IsFolder &&
                                  !IsBinaryFile(_selectedItem.Name) &&
                                  !IsBusy;
            RenameCommand.Enabled = hasLoadedImage &&
                                    (_selectedItem != null && (hasSelection || hasTreeSelection)) &&
                                    !IsBusy;
            BootSectorCommand.Enabled = hasLoadedImage && !IsBusy;
            PartitionsCommand.Enabled = hasLoadedImage && _imageWorker!.Partitions.Count > 0 && !IsBusy;
            ImageMapCommand.Enabled = hasLoadedImage && !IsBusy;
            SaveCommand.Enabled = hasLoadedImage && _canSaveCurrentImage && !IsBusy;
            SaveAsCommand.Enabled = hasLoadedImage && !IsBusy;
        }

        private static long CalculateDirectorySize(string hostPath)
        {
            long totalSize = 0;
            foreach (var file in Directory.EnumerateFiles(hostPath, "*", SearchOption.AllDirectories))
            {
                totalSize += new FileInfo(file).Length;
            }

            return totalSize;
        }

        /// <summary>Calculates the total size of a folder inside the disk image.</summary>
        private long CalculateFolderSize(string folderPath)
        {
            if (_imageWorker == null)
                return 0;

            var prefix = string.IsNullOrEmpty(folderPath) ? string.Empty : folderPath + "\\";
            return _imageWorker.FilesAndFolders
                .Where(entry => string.IsNullOrEmpty(prefix) || entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(entry => !string.IsNullOrEmpty(folderPath) || entry.Path != folderPath)
                .Sum(entry => entry.Size ?? 0L);
        }

        private static string BuildNotEnoughSpaceMessage(string sizeLabel, long requiredBytes, long freeBytes)
        {
            return $"Not enough space on the disk image.\n\n" +
                   $"{sizeLabel}: {requiredBytes:N0} bytes\n" +
                   $"Free space: {freeBytes:N0} bytes";
        }

        private void RefreshDiskSpaceText()
        {
            if (_imageWorker == null || !_imageWorker.IsLoaded)
            {
                ClearDiskSpaceText();
                return;
            }

            FilesystemTypeText = _imageWorker.FilesystemType?.ToString() ?? string.Empty;
            DiskSpaceText = $"Total: {FormatBytes(_imageWorker.GetTotalBytes())} | Free: {FormatBytes(_imageWorker.GetFreeBytes())}";
        }

        private void ClearDiskSpaceText()
        {
            DiskSpaceText = string.Empty;
            FilesystemTypeText = string.Empty;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";

            if (bytes < 1024L * 1024)
                return $"{bytes / 1024.0:F1} KB";

            if (bytes < 1024L * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024):F1} MB";

            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }

        private static string GetApplicationVersion()
        {
            var assembly = typeof(MainViewModel).Assembly;
            var version = assembly.GetName().Version;
            return version == null ? "0.1" : $"{version.Major}.{version.Minor}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
