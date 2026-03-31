using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using NetImage.Models;
using NetImage.ViewModels;
using RenameRequestEventArgs = NetImage.ViewModels.RenameRequestEventArgs;

namespace NetImage.Views
{
    /// <summary>
    /// Interaction logic for MainView.xaml
    /// </summary>
    public partial class MainView : Window
    {
        private string? _currentSortProp;
        private ListSortDirection _currentSortDirection = ListSortDirection.Ascending;

        public MainView()
        {
            InitializeComponent();
            MainTreeView.SelectedItemChanged += OnTreeViewSelectedItemChanged;
            EventManager.RegisterClassHandler(typeof(TreeViewItem), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnTreeViewItemLoaded));
            EventManager.RegisterClassHandler(typeof(GridViewColumnHeader), MouseLeftButtonUpEvent, new MouseButtonEventHandler(GridViewHeader_MouseLeftButtonUp));

            // Set default sort: folders first, then by name
            ApplyDefaultSort();
        }

        private void ApplyDefaultSort()
        {
            var items = MainListView.Items;
            if (items != null)
            {
                items.SortDescriptions.Add(new SortDescription("IsFolder", ListSortDirection.Descending));
            }
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            if (DataContext is MainViewModel vm)
            {
                vm.CreateFolderRequested += OnCreateFolderRequested;
                vm.CreateFolderError += OnCreateFolderError;
                vm.AddError += OnAddError;
                vm.DeleteError += OnDeleteError;
                vm.ExtractError += OnExtractError;
                vm.SaveError += OnSaveError;
                vm.CloseImageRequested += OnCloseImageRequested;
                vm.RenameRequested += OnRenameRequested;
                vm.RenameError += OnRenameError;
                vm.TreeViewBuilt += OnTreeViewBuilt;

                // Handle command-line file path (for file associations)
                var filePath = App.CommandLineFilePath;
                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                {
                    _ = vm.OpenImageFileAsync(filePath);
                }
            }

            DataContextChanged += (_, e) =>
            {
                if (e.OldValue is MainViewModel oldVm)
                {
                    oldVm.CreateFolderRequested -= OnCreateFolderRequested;
                    oldVm.CreateFolderError -= OnCreateFolderError;
                    oldVm.AddError -= OnAddError;
                    oldVm.DeleteError -= OnDeleteError;
                    oldVm.ExtractError -= OnExtractError;
                    oldVm.SaveError -= OnSaveError;
                    oldVm.CloseImageRequested -= OnCloseImageRequested;
                }
                if (e.NewValue is MainViewModel newVm)
                {
                    newVm.CreateFolderRequested += OnCreateFolderRequested;
                    newVm.CreateFolderError += OnCreateFolderError;
                    newVm.AddError += OnAddError;
                    newVm.DeleteError += OnDeleteError;
                    newVm.ExtractError += OnExtractError;
                    newVm.SaveError += OnSaveError;
                    newVm.CloseImageRequested += OnCloseImageRequested;
                    newVm.RenameRequested += OnRenameRequested;
                    newVm.RenameError += OnRenameError;
                    newVm.TreeViewBuilt += OnTreeViewBuilt;
                }
            };
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (DataContext is MainViewModel vm && vm.HasUnsavedChanges)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Do you want to save them before closing?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (result == MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    vm.SaveCommand.Execute(null);
                }
            }
        }

        private void OnTreeViewSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is MainViewModel vm)
            {
                var folder = e.NewValue as TreeItem;
                vm.CurrentFolder = folder;
                vm.SelectedItem = folder;
                MainListView.SelectedItem = null;
            }
        }

        private void MainListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                // Update SelectedItems collection for multiselect support
                var selectedItemsList = MainListView.SelectedItems.OfType<TreeItem>().ToList();

                // Clear and repopulate the collection
                while (vm.SelectedItems.Count > 0)
                {
                    vm.SelectedItems.RemoveAt(0);
                }

                foreach (var item in selectedItemsList)
                {
                    vm.SelectedItems.Add(item);
                }

                // Also update SelectedItem for backwards compatibility (first selected item)
                if (selectedItemsList.Count > 0)
                {
                    vm.SelectedItem = selectedItemsList[0];
                }
                else
                {
                    vm.SelectedItem = MainTreeView.SelectedItem as TreeItem;
                }
            }
        }

        private void MainListViewItem_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not ListViewItem { Content: TreeItem item } || DataContext is not MainViewModel vm)
            {
                return;
            }

            vm.SelectedItem = item;

            if (!item.IsFolder)
            {
                if (vm.ExtractCommand.CanExecute(null))
                {
                    vm.ExtractCommand.Execute(null);
                }

                e.Handled = true;
                return;
            }

            if (vm.CurrentFolder != null)
            {
                vm.CurrentFolder.IsExpanded = true;
                MainTreeView.UpdateLayout();
            }

            item.IsSelected = true;
            item.IsExpanded = true;
        }

        private void OnCreateFolderRequested(object? sender, CreateFolderRequestEventArgs e)
        {
            var dialog = new FolderNameDialog { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                e.FolderName = dialog.FolderName;
            }
        }

        private void OnCreateFolderError(object? sender, string message)
        {
            MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnAddError(object? sender, string message)
        {
            MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnDeleteError(object? sender, string message)
        {
            MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnExtractError(object? sender, string message)
        {
            MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnSaveError(object? sender, string message)
        {
            MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnRenameRequested(object? sender, RenameRequestEventArgs e)
        {
            var dialog = new RenameDialog { Owner = this };
            dialog.SetItemInfo(e.Item.Name, e.Item.IsFolder);
            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.NewName))
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.PerformRename(e.Item, dialog.NewName);
                }
            }
        }

        private void OnRenameError(object? sender, string message)
        {
            MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnTreeViewBuilt(object? sender, EventArgs e)
        {
            // Force expand the current folder in the tree view after it's been rebuilt
            // Use multiple Dispatcher invokes to allow visual tree to materialize between expansions
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (DataContext is MainViewModel vm && vm.CurrentFolder != null)
                {
                    ExpandFolderPathInTreeView(MainTreeView, vm.CurrentFolder);
                }
            }));
        }

        /// <summary>
        /// Expands all parent folders of the target folder by traversing the data model.
        /// Uses sequential Dispatcher invokes to allow visual tree items to materialize.
        /// </summary>
        private void ExpandFolderPathInTreeView(TreeView treeView, TreeItem targetFolder)
        {
            // Build the path from root to target by walking parent references
            var path = new List<TreeItem>();
            var current = targetFolder;
            while (current != null)
            {
                path.Add(current);
                // Find parent by searching TreeItems collection
                current = FindParent(treeView.Items, current);
            }
            path.Reverse();

            // Expand each folder in the path sequentially
            ExpandPathSequentially(treeView, path, 0);
        }

        private TreeItem? FindParent(IEnumerable items, TreeItem child)
        {
            foreach (var item in items)
            {
                if (item is TreeItem parent)
                {
                    if (parent.Children.Contains(child))
                        return parent;
                    // Recursively search children
                    var found = FindParent(parent.Children, child);
                    if (found != null)
                        return found;
                }
            }
            return null;
        }

        private void ExpandPathSequentially(TreeView treeView, List<TreeItem> path, int index)
        {
            if (index >= path.Count)
                return;

            var targetItem = path[index];

            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Find the TreeViewItem for this data item
                var treeViewItem = FindTreeViewItemForItem(treeView, targetItem);
                if (treeViewItem != null)
                {
                    treeViewItem.IsExpanded = true;
                }

                // Schedule the next expansion after this one completes
                if (index < path.Count - 1)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                        ExpandPathSequentially(treeView, path, index + 1)));
                }
            }));
        }

        private TreeViewItem? FindTreeViewItemForItem(DependencyObject parent, TreeItem targetDataItem)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);

                if (child is TreeViewItem treeViewItem)
                {
                    if (treeViewItem.DataContext == targetDataItem)
                        return treeViewItem;

                    // Search in children
                    var found = FindTreeViewItemForItem(treeViewItem, targetDataItem);
                    if (found != null)
                        return found;
                }
                else if (child is Panel panel)
                {
                    for (int j = 0; j < System.Windows.Media.VisualTreeHelper.GetChildrenCount(panel); j++)
                    {
                        var grandChild = System.Windows.Media.VisualTreeHelper.GetChild(panel, j);
                        if (grandChild is TreeViewItem gv)
                        {
                            if (gv.DataContext == targetDataItem)
                                return gv;
                            var found = FindTreeViewItemForItem(gv, targetDataItem);
                            if (found != null)
                                return found;
                        }
                    }
                }
            }
            return null;
        }

        private void OnCloseImageRequested(object? sender, CloseImageRequestEventArgs e)
        {
            var result = MessageBox.Show(
                "You have unsaved changes. Do you want to save them before closing?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            switch (result)
            {
                case MessageBoxResult.Yes:
                    e.AllowClose = true;
                    e.SaveChanges = true;
                    break;
                case MessageBoxResult.No:
                    e.AllowClose = true;
                    e.SaveChanges = false;
                    break;
                case MessageBoxResult.Cancel:
                    e.AllowClose = false;
                    e.SaveChanges = false;
                    break;
            }
        }

        private void GridViewHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not GridViewColumnHeader header ||
                header.Column?.Header is not FrameworkElement { Tag: string sortProp })
            {
                return;
            }

            // Toggle direction if same column, otherwise default to ascending
            if (_currentSortProp == sortProp)
            {
                _currentSortDirection = _currentSortDirection == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
            }
            else
            {
                _currentSortDirection = ListSortDirection.Ascending;
                _currentSortProp = sortProp;
            }

            // Apply sort using the ListView's Items collection view
            var items = MainListView.Items;
            if (items != null)
            {
                items.SortDescriptions.Clear();
                // Always sort folders to the top first (IsFolder descending: true > false)
                items.SortDescriptions.Add(new SortDescription("IsFolder", ListSortDirection.Descending));
                // Then apply the user's chosen sort criteria
                items.SortDescriptions.Add(new SortDescription(sortProp, _currentSortDirection));
            }
        }

        private static void OnTreeViewItemLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is TreeViewItem item)
            {
                var cp = FindVisualChild<ContentPresenter>(item);
                if (cp != null)
                {
                    cp.HorizontalAlignment = HorizontalAlignment.Stretch;
                    var parent = System.Windows.Media.VisualTreeHelper.GetParent(cp);
                    if (parent is Grid grid)
                    {
                        var col = Grid.GetColumn(cp);
                        if (grid.ColumnDefinitions.Count > col)
                        {
                            grid.ColumnDefinitions[col].Width = new GridLength(1, GridUnitType.Star);
                        }
                    }
                }
            }
        }

        private static T? FindVisualChild<T>(DependencyObject obj) where T : FrameworkElement
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(obj, i);
                if (child is T t)
                    return t;
                var result = FindVisualChild<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }
    }
}
