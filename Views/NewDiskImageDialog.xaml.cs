using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace NetImage.Views
{
    public partial class NewDiskImageDialog : Window
    {
        public long SelectedSize { get; private set; }
        public bool IsHardDisk { get; private set; }
        public uint Cylinders { get; private set; }
        public uint Heads { get; private set; }
        public uint SectorsPerTrack { get; private set; }

        private long _calculatedSize = 0;

        public NewDiskImageDialog()
        {
            InitializeComponent();
            ValidateChs(null, null);
        }

        private void TabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Determine which tab is selected and set IsHardDisk accordingly
            if (sender is TabControl tabControl && tabControl.SelectedItem is TabItem selectedTab)
            {
                IsHardDisk = selectedTab.Header?.ToString() == "Hard Disk";

                // Validate CHS when switching to hard disk tab
                if (IsHardDisk)
                {
                    ValidateChs(null, null);
                }
            }
        }

        private void ValidateChs(object? sender, RoutedEventArgs? e)
        {
            CylindersError.Text = string.Empty;
            HeadsError.Text = string.Empty;
            SectorsError.Text = string.Empty;

            bool valid = true;

            if (!uint.TryParse(CylindersBox.Text, out uint cylinders) || cylinders == 0)
            {
                CylindersError.Text = "Required (1-16383)";
                valid = false;
            }
            else if (cylinders > 16383)
            {
                CylindersError.Text = "Max 16383";
                valid = false;
            }

            if (!uint.TryParse(HeadsBox.Text, out uint heads) || heads == 0)
            {
                HeadsError.Text = "Required (1-255)";
                valid = false;
            }
            else if (heads > 255)
            {
                HeadsError.Text = "Max 255";
                valid = false;
            }

            if (!uint.TryParse(SectorsBox.Text, out uint sectors) || sectors == 0)
            {
                SectorsError.Text = "Required (1-255)";
                valid = false;
            }
            else if (sectors > 255)
            {
                SectorsError.Text = "Max 255";
                valid = false;
            }

            if (valid)
            {
                _calculatedSize = (long)cylinders * heads * sectors * 512;
                var sizeMB = _calculatedSize / (1024.0 * 1024.0);
                SizeDisplay.Text = $"{sizeMB:F2} MB ({_calculatedSize:N0} bytes)";
            }
            else
            {
                _calculatedSize = 0;
                SizeDisplay.Text = "Invalid geometry";
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // Determine which tab is selected
            var tabControl = MainGrid.Children.OfType<TabControl>().FirstOrDefault();
            if (tabControl?.SelectedItem is TabItem selectedTab)
            {
                IsHardDisk = selectedTab.Header?.ToString() == "Hard Disk";

                if (IsHardDisk)
                {
                    ValidateChs(null, null);

                    if (_calculatedSize > 0 &&
                        uint.TryParse(CylindersBox.Text, out var cylinders) &&
                        uint.TryParse(HeadsBox.Text, out var heads) &&
                        uint.TryParse(SectorsBox.Text, out var sectors))
                    {
                        SelectedSize = _calculatedSize;
                        Cylinders = cylinders;
                        Heads = heads;
                        SectorsPerTrack = sectors;
                        DialogResult = true;
                        Close();
                        return;
                    }
                }
                else
                {
                    // Floppy disk - find selected radio button in the tab content
                    var tabContent = selectedTab.Content as StackPanel;
                    if (tabContent != null)
                    {
                        foreach (RadioButton radio in tabContent.Children.OfType<RadioButton>())
                        {
                            if (radio.IsChecked == true)
                            {
                                if (long.TryParse(radio.Tag?.ToString(), out long size))
                                {
                                    SelectedSize = size;
                                    DialogResult = true;
                                    Close();
                                    return;
                                }
                            }
                        }
                    }
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
