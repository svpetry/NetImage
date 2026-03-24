using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using NetImage.Models;
using NetImage.Utils;
using NetImage.Workers;

namespace NetImage.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private const string ApplicationName = "NetImage";
        private string _statusText = "Ready";
        private string _diskSpaceText = string.Empty;
        private DiskImageWorker? _imageWorker;
        private TreeItem? _currentFolder;
        private TreeItem? _selectedItem;

        public event EventHandler<string>? AddError;

        public event EventHandler<CreateFolderRequestEventArgs>? CreateFolderRequested;
        public event EventHandler<string>? CreateFolderError;
        public event EventHandler<string>? DeleteError;

        public event EventHandler<string>? ExtractError;

        public event EventHandler<string>? SaveError;

        public MainViewModel()
        {
            OpenCommand = new ActionCommand(ExecuteOpen);
            CloseCommand = new ActionCommand(ExecuteClose) { Enabled = false };
            AddCommand = new ActionCommand(ExecuteAdd) { Enabled = false };
            AddFolderCommand = new ActionCommand(ExecuteAddFolder) { Enabled = false };
            CreateFolderCommand = new ActionCommand(ExecuteCreateFolder) { Enabled = false };
            DeleteCommand = new ActionCommand(ExecuteDelete) { Enabled = false };
            ExtractCommand = new ActionCommand(ExecuteExtract) { Enabled = false };
            SaveCommand = new ActionCommand(ExecuteSave) { Enabled = false };
            SaveAsCommand = new ActionCommand(ExecuteSaveAs) { Enabled = false };
            TreeItems = new ObservableCollection<TreeItem>();
        }

        public ActionCommand OpenCommand { get; }
        public ActionCommand CloseCommand { get; }
        public ActionCommand AddCommand { get; }
        public ActionCommand AddFolderCommand { get; }
        public ActionCommand CreateFolderCommand { get; }
        public ActionCommand DeleteCommand { get; }
        public ActionCommand ExtractCommand { get; }
        public ActionCommand SaveCommand { get; }
        public ActionCommand SaveAsCommand { get; }
        public string WindowTitle => $"{ApplicationName} {GetApplicationVersion()}";

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

        public ObservableCollection<TreeItem> TreeItems { get; }

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
                DeleteCommand.Enabled = value != null;
                ExtractCommand.Enabled = value != null;
            }
        }

        private async void ExecuteOpen(object? parameter)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Disk Image Files|*.ima;*.img|All Files|*.*"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                ClearDiskSpaceText();
                _imageWorker = new DiskImageWorker(openFileDialog.FileName);
                _imageWorker.LoadingStarted += OnLoadingStarted;
                _imageWorker.LoadingCompleted += OnLoadingCompleted;

                await _imageWorker.OpenAsync();

                BuildTreeView();
                RefreshDiskSpaceText();
                CloseCommand.Enabled = true;
                AddCommand.Enabled = true;
                AddFolderCommand.Enabled = true;
                CreateFolderCommand.Enabled = true;
                SaveCommand.Enabled = true;
                SaveAsCommand.Enabled = true;
                StatusText = $"Opened: {openFileDialog.FileName}";
            }
        }

        private void BuildTreeView()
        {
            TreeItems.Clear();

            if (_imageWorker == null || _imageWorker.FilesAndFolders.Count == 0)
            {
                return;
            }

            var rootNode = new TreeItem(_imageWorker.VolumeLabel, string.Empty);
            var nodeByPath = new Dictionary<string, TreeItem>(StringComparer.OrdinalIgnoreCase);
            nodeByPath[""] = rootNode;

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

            rootNode.IsSelected = true;
            rootNode.IsExpanded = true;
            TreeItems.Add(rootNode);
            SelectedItem = rootNode;
        }

        private void ExecuteClose(object? parameter)
        {
            _imageWorker = null;
            TreeItems.Clear();
            CloseCommand.Enabled = false;
            AddCommand.Enabled = false;
            AddFolderCommand.Enabled = false;
            CreateFolderCommand.Enabled = false;
            DeleteCommand.Enabled = false;
            ExtractCommand.Enabled = false;
            SaveCommand.Enabled = false;
            SaveAsCommand.Enabled = false;
            CurrentFolder = null;
            SelectedItem = null;
            ClearDiskSpaceText();
            StatusText = "Ready";
        }

        private void ExecuteCreateFolder(object? parameter)
        {
            if (_imageWorker == null)
                return;

            var args = new CreateFolderRequestEventArgs();
            CreateFolderRequested?.Invoke(this, args);

            if (string.IsNullOrWhiteSpace(args.FolderName))
                return;

            var targetDir = GetSelectedFolderPath();
            
            try
            {
                _imageWorker.CreateFolder(targetDir, args.FolderName);
            }
            catch (Exception ex)
            {
                CreateFolderError?.Invoke(this, $"Could not create folder:\n{ex.Message}");
                return;
            }

            BuildTreeView();
            RefreshDiskSpaceText();

            var location = string.IsNullOrEmpty(targetDir) ? "root" : targetDir;
            StatusText = $"Created folder '{args.FolderName}' in {location}";
        }

        private void ExecuteAdd(object? parameter)
        {
            if (_imageWorker == null)
                return;

            var openFileDialog = new OpenFileDialog
            {
                Title = "Select file to add to disk image",
                Filter = "All Files|*.*"
            };

            if (openFileDialog.ShowDialog() != true)
                return;

            var hostPath = openFileDialog.FileName;
            var fileName = System.IO.Path.GetFileName(hostPath);
            byte[] content;

            try
            {
                content = System.IO.File.ReadAllBytes(hostPath);
            }
            catch (Exception ex)
            {
                AddError?.Invoke(this, $"Could not read the selected file:\n{ex.Message}");
                return;
            }

            // Pre-flight free-space check
            var freeBytes = _imageWorker.GetFreeBytes();
            if (content.Length > freeBytes)
            {
                AddError?.Invoke(this,
                    $"Not enough space on the disk image.\n\n" +
                    $"File size:  {content.Length:N0} bytes\n" +
                    $"Free space: {freeBytes:N0} bytes");
                return;
            }

            var targetDir = GetSelectedFolderPath();

            try
            {
                _imageWorker.AddFile(targetDir, fileName, content);
            }
            catch (InvalidOperationException ex)
            {
                AddError?.Invoke(this, ex.Message);
                return;
            }

            BuildTreeView();
            RefreshDiskSpaceText();

            var location = string.IsNullOrEmpty(targetDir) ? "root" : targetDir;
            StatusText = $"Added '{fileName}' to {location}";
        }

        private void ExecuteAddFolder(object? parameter)
        {
            if (_imageWorker == null)
                return;

            var folderDialog = new OpenFolderDialog
            {
                Title = "Select folder to add to disk image"
            };

            if (folderDialog.ShowDialog() != true)
                return;

            var hostPath = folderDialog.FolderName;
            var folderName = new DirectoryInfo(hostPath).Name;

            var targetDir = GetSelectedFolderPath();

            // Calculate total size
            long totalSize = 0;
            try
            {
                var files = Directory.GetFiles(hostPath, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    totalSize += new FileInfo(file).Length;
                }
            }
            catch (Exception ex)
            {
                AddError?.Invoke(this, $"Could not read the selected folder:\n{ex.Message}");
                return;
            }

            var freeBytes = _imageWorker.GetFreeBytes();
            if (totalSize > freeBytes)
            {
                AddError?.Invoke(this,
                    $"Not enough space on the disk image.\n\n" +
                    $"Folder size: {totalSize:N0} bytes\n" +
                    $"Free space:  {freeBytes:N0} bytes");
                return;
            }

            try
            {
                _imageWorker.AddHostDirectory(targetDir, hostPath);
            }
            catch (InvalidOperationException ex)
            {
                AddError?.Invoke(this, ex.Message);
                return;
            }

            BuildTreeView();
            RefreshDiskSpaceText();

            var location = string.IsNullOrEmpty(targetDir) ? "root" : targetDir;
            StatusText = $"Added folder '{folderName}' to {location}";
        }

        private void ExecuteDelete(object? parameter)
        {
            if (_imageWorker == null || _selectedItem == null)
                return;

            var item = _selectedItem;

            try
            {
                _imageWorker.DeleteEntry(item.Path);
            }
            catch (Exception ex)
            {
                DeleteError?.Invoke(this, ex.Message);
                return;
            }

            BuildTreeView();
            RefreshDiskSpaceText();
            StatusText = $"Deleted '{item.Name}'";
        }

        private void ExecuteExtract(object? parameter)
        {
            if (_imageWorker == null || _selectedItem == null)
                return;

            var item = _selectedItem;

            if (item.IsFolder)
            {
                var folderDialog = new OpenFolderDialog
                {
                    Title = "Select Destination Folder"
                };

                if (folderDialog.ShowDialog() == true)
                {
                    try
                    {
                        var destPath = string.IsNullOrEmpty(item.Path) ? folderDialog.FolderName : System.IO.Path.Combine(folderDialog.FolderName, item.Name);
                        _imageWorker.ExtractFolder(item.Path, destPath);
                        StatusText = $"Extracted folder '{item.Name}' to '{destPath}'";
                    }
                    catch (Exception ex)
                    {
                        ExtractError?.Invoke(this, $"Failed to extract folder:\n{ex.Message}");
                    }
                }
            }
            else
            {
                var content = _imageWorker.GetFileContent(item.Path);

                if (content == null)
                {
                    ExtractError?.Invoke(this, $"Could not extract '{item.Name}'. File not found or read error.");
                    return;
                }

                var saveFileDialog = new SaveFileDialog
                {
                    Title = "Extract File",
                    FileName = item.Name,
                    Filter = "All Files|*.*"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    try
                    {
                        System.IO.File.WriteAllBytes(saveFileDialog.FileName, content);
                        StatusText = $"Extracted '{item.Name}' to '{saveFileDialog.FileName}'";
                    }
                    catch (Exception ex)
                    {
                        ExtractError?.Invoke(this, $"Failed to save extracted file:\n{ex.Message}");
                    }
                }
            }
        }

        private async void ExecuteSave(object? parameter)
        {
            if (_imageWorker == null)
                return;

            try
            {
                await _imageWorker.SaveAsync(_imageWorker.FilePath);
                StatusText = $"Saved: {_imageWorker.FilePath}";
            }
            catch (Exception ex)
            {
                SaveError?.Invoke(this, $"Could not save image:\n{ex.Message}");
            }
        }

        private async void ExecuteSaveAs(object? parameter)
        {
            if (_imageWorker == null)
                return;

            var saveFileDialog = new SaveFileDialog
            {
                Title = "Save Disk Image As",
                Filter = "Disk Image Files|*.ima;*.img|All Files|*.*",
                FileName = System.IO.Path.GetFileName(_imageWorker.FilePath)
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    await _imageWorker.SaveAsync(saveFileDialog.FileName);
                    _imageWorker.FilePath = saveFileDialog.FileName;
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
            if (_selectedItem.Size == null)
                return _selectedItem.Path;

            // A file: strip the last segment to get the parent folder
            var lastSlash = _selectedItem.Path.LastIndexOf('\\');
            return lastSlash >= 0 ? _selectedItem.Path[..lastSlash] : string.Empty;
        }

        private void OnLoadingStarted(object? sender, EventArgs e)
        {
            ClearDiskSpaceText();
            StatusText = "Loading";
        }

        private void OnLoadingCompleted(object? sender, EventArgs e)
        {
            StatusText = "Ready";
        }

        private void RefreshDiskSpaceText()
        {
            if (_imageWorker == null || !_imageWorker.IsLoaded)
            {
                ClearDiskSpaceText();
                return;
            }

            DiskSpaceText = $"Total: {FormatBytes(_imageWorker.GetTotalBytes())} | Free: {FormatBytes(_imageWorker.GetFreeBytes())}";
        }

        private void ClearDiskSpaceText()
        {
            DiskSpaceText = string.Empty;
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
            var informationalVersion = Attribute.GetCustomAttribute(
                assembly,
                typeof(System.Reflection.AssemblyInformationalVersionAttribute))
                as System.Reflection.AssemblyInformationalVersionAttribute;

            if (!string.IsNullOrWhiteSpace(informationalVersion?.InformationalVersion))
                return informationalVersion.InformationalVersion;

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
