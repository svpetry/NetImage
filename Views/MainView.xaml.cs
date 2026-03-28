using System;
using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using NetImage.Models;
using NetImage.ViewModels;

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
                }
                if (e.NewValue is MainViewModel newVm)
                {
                    newVm.CreateFolderRequested += OnCreateFolderRequested;
                    newVm.CreateFolderError += OnCreateFolderError;
                    newVm.AddError += OnAddError;
                    newVm.DeleteError += OnDeleteError;
                    newVm.ExtractError += OnExtractError;
                    newVm.SaveError += OnSaveError;
                }
            };
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
                if (e.AddedItems.Count > 0 && e.AddedItems[0] is TreeItem item)
                {
                    vm.SelectedItem = item;
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
