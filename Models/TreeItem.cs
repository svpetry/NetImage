using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NetImage.Models
{
    /// <summary>Segoe Fluent Icons glyph codes.</summary>
    internal static class IconGlyphs
    {
        public const string Drive = "\xEDA2";      // Hard drive icon
        public const string Folder = "\xE8B7";     // Folder icon
        public const string File = "\xE7C3";       // Document/file icon
    }

    public class TreeItem : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private bool _isDropTarget;
        private bool _isSelected;

        public TreeItem(string name, string path, long? size = null, DateTime? modified = null)
        {
            Name = name;
            Path = path;
            Size = size;
            Modified = modified;
            Children = new ObservableCollection<TreeItem>();
            Items = new ObservableCollection<TreeItem>();
        }

        public string Name { get; }

        /// <summary>Full backslash-separated path inside the disk image (e.g. "SUBDIR\FILE.TXT"). Empty string = root level.</summary>
        public string Path { get; }

        public long? Size { get; }

        public DateTime? Modified { get; }

        public string FormattedModified => Modified?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

        public string FormattedSize
        {
            get
            {
                if (Size == null) return string.Empty;
                var bytes = Size.Value;
                if (bytes < 1024) return $"{bytes} B";
                if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
                return $"{bytes / (1024.0 * 1024):F1} MB";
            }
        }

        public bool IsFolder => Size == null;

        public string IconGlyph => Path == "" ? IconGlyphs.Drive : (IsFolder ? IconGlyphs.Folder : IconGlyphs.File);

        public ObservableCollection<TreeItem> Children { get; }
        
        public ObservableCollection<TreeItem> Items { get; }
        
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public bool IsDropTarget
        {
            get => _isDropTarget;
            set
            {
                if (_isDropTarget == value)
                {
                    return;
                }

                _isDropTarget = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
