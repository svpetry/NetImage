using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace NetImage.Views
{
    public partial class BootSectorDialog : Window
    {
        private byte[] _bootSector = new byte[512];
        private readonly TextBox[] _hexBoxes = new TextBox[512];
        private readonly TextBox[] _asciiBoxes = new TextBox[512];

        public byte[]? EditedBootSector { get; private set; }

        public BootSectorDialog()
        {
            InitializeComponent();
            DataContext = this;
            BuildHexEditor();
        }

        public void SetBootSector(byte[] bootSector)
        {
            if (bootSector == null || bootSector.Length != 512)
                throw new ArgumentException("Boot sector must be exactly 512 bytes.");

            _bootSector = (byte[])bootSector.Clone();
            UpdateHexEditor();
        }

        private void BuildHexEditor()
        {
            // Create rows for each 16-byte line (32 rows for 512 bytes)
            for (int row = 0; row < 32; row++)
            {
                int baseOffset = row * 16;

                // Create a row container grid for offset and hex values
                var rowGrid = new Grid();
                rowGrid.Background = Brushes.Transparent;
                rowGrid.Margin = new Thickness(0, 0, 0, 2);

                // Column definitions: offset + 16 hex columns
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) }); // offset
                for (int i = 0; i < 16; i++)
                {
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); // hex
                }

                // Offset column
                var offsetText = new TextBlock
                {
                    Text = $"{baseOffset:X3}:",
                    Foreground = Brushes.Gray,
                    FontFamily = new FontFamily("Lucida Console"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(offsetText, 0);
                rowGrid.Children.Add(offsetText);

                // Hex TextBoxes
                for (int col = 0; col < 16; col++)
                {
                    int offset = baseOffset + col;

                    var hexBox = new TextBox
                    {
                        Width = 26,
                        Height = 18,
                        FontFamily = new FontFamily("Lucida Console"),
                        FontSize = 12,
                        Background = Brushes.Transparent,
                        Foreground = Brushes.White,
                        BorderThickness = new Thickness(0),
                        TextAlignment = TextAlignment.Center,
                        VerticalContentAlignment = VerticalAlignment.Center
                    };

                    hexBox.PreviewKeyDown += HexBox_PreviewKeyDown;
                    hexBox.TextChanged += HexBox_TextChanged;
                    hexBox.GotFocus += HexBox_GotFocus;
                    hexBox.LostFocus += HexBox_LostFocus;

                    _hexBoxes[offset] = hexBox;
                    Grid.SetColumn(hexBox, col + 1);
                    rowGrid.Children.Add(hexBox);
                }

                OffsetAndHexPanel.Children.Add(rowGrid);

                // Create ASCII row in separate grid
                var asciiRowGrid = new Grid();
                asciiRowGrid.Background = Brushes.Transparent;
                asciiRowGrid.Margin = new Thickness(0, 0, 0, 2);

                for (int i = 0; i < 16; i++)
                {
                    asciiRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                }

                for (int col = 0; col < 16; col++)
                {
                    int offset = baseOffset + col;

                    var asciiBox = new TextBox
                    {
                        Width = 12,
                        Height = 18,
                        FontFamily = new FontFamily("Lucida Console"),
                        FontSize = 12,
                        Background = Brushes.Transparent,
                        Foreground = Brushes.White,
                        BorderThickness = new Thickness(0),
                        TextAlignment = TextAlignment.Center,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        MaxLength = 1
                    };

                    asciiBox.PreviewKeyDown += AsciiBox_PreviewKeyDown;
                    asciiBox.TextChanged += AsciiBox_TextChanged;
                    asciiBox.GotFocus += AsciiBox_GotFocus;
                    asciiBox.LostFocus += AsciiBox_LostFocus;

                    _asciiBoxes[offset] = asciiBox;
                    Grid.SetColumn(asciiBox, col);
                    asciiRowGrid.Children.Add(asciiBox);
                }

                AsciiPanel.Children.Add(asciiRowGrid);
            }
        }

        private void UpdateHexEditor()
        {
            for (int i = 0; i < 512; i++)
            {
                // Update hex value
                _hexBoxes[i].Text = _bootSector[i].ToString("X2");

                // Update ASCII character (printable range 32-126, or '.' for non-printable)
                byte b = _bootSector[i];
                char c = (b >= 32 && b <= 126) ? (char)b : '.';
                _asciiBoxes[i].Text = c.ToString();

                // Color non-printable characters differently
                if (b < 32 || b > 126)
                {
                    _asciiBoxes[i].Foreground = Brushes.Gray;
                }
                else
                {
                    _asciiBoxes[i].Foreground = Brushes.White;
                }
            }
        }

        private void HexBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box) return;

            // Handle navigation
            switch (e.Key)
            {
                case Key.Left:
                    MoveFocus(box, -1, _hexBoxes);
                    e.Handled = true;
                    break;
                case Key.Right:
                    MoveFocus(box, 1, _hexBoxes);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveFocus(box, -16, _hexBoxes);
                    e.Handled = true;
                    break;
                case Key.Down:
                    MoveFocus(box, 16, _hexBoxes);
                    e.Handled = true;
                    break;
                case Key.Home:
                    box.SelectAll();
                    e.Handled = true;
                    break;
                case Key.End:
                    MoveCaretToEnd(box);
                    e.Handled = true;
                    break;
                case Key.Tab:
                    // Move to ASCII box for same offset
                    int offset = GetOffset(box, _hexBoxes);
                    if (offset >= 0 && offset < 512)
                    {
                        _asciiBoxes[offset].Focus();
                        _asciiBoxes[offset].SelectAll();
                    }
                    e.Handled = true;
                    break;
            }
        }

        private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox box || !box.IsFocused) return;

            string text = box.Text;
            if (!IsValidHex(text))
            {
                // Revert invalid keypress
                int offset = GetOffset(box, _hexBoxes);
                if (offset >= 0 && offset < 512)
                {
                    box.Text = _bootSector[offset].ToString("X2");
                    box.SelectAll();
                }
                return;
            }

            if (text.Length == 2)
            {
                // Auto-move to next box
                int offset = GetOffset(box, _hexBoxes);
                if (offset >= 0 && offset < 511)
                {
                    _hexBoxes[offset + 1].Focus();
                }
            }
        }

        private void AsciiBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box) return;

            // Handle navigation
            switch (e.Key)
            {
                case Key.Left:
                    MoveFocus(box, -1, _asciiBoxes);
                    e.Handled = true;
                    break;
                case Key.Right:
                    MoveFocus(box, 1, _asciiBoxes);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveFocus(box, -16, _asciiBoxes);
                    e.Handled = true;
                    break;
                case Key.Down:
                    MoveFocus(box, 16, _asciiBoxes);
                    e.Handled = true;
                    break;
                case Key.Tab:
                    // Move to hex box for same offset
                    int offset = GetOffset(box, _asciiBoxes);
                    if (offset >= 0 && offset < 512)
                    {
                        _hexBoxes[offset].Focus();
                        _hexBoxes[offset].SelectAll();
                    }
                    e.Handled = true;
                    break;
            }
        }

        private void AsciiBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox box || !box.IsFocused) return;

            if (box.Text.Length == 1)
            {
                int offset = GetOffset(box, _asciiBoxes);
                if (offset >= 0 && offset < 512)
                {
                    char c = box.Text[0];
                    byte b = (byte)c;
                    _bootSector[offset] = b;
                    _hexBoxes[offset].Text = b.ToString("X2");

                    if (c >= 32 && c <= 126)
                    {
                        box.Foreground = Brushes.White;
                    }
                    else
                    {
                        box.Foreground = Brushes.Gray;
                    }

                    if (offset < 511)
                    {
                        _asciiBoxes[offset + 1].Focus();
                    }
                }
            }
        }

        private void HexBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box)
            {
                box.Background = Brushes.DarkSlateBlue;
                box.SelectAll();
            }
        }

        private void AsciiBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box)
            {
                box.Background = Brushes.DarkSlateBlue;
                box.Foreground = Brushes.White;
                box.SelectAll();
            }
        }

        private void HexBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box) return;

            box.Background = Brushes.Transparent;

            // Validate and finalize hex value
            string text = box.Text;
            int offset = GetOffset(box, _hexBoxes);
            if (offset < 0 || offset >= 512) return;

            if (IsValidHex(text) && text.Length == 2)
            {
                byte b = byte.Parse(text, System.Globalization.NumberStyles.HexNumber);
                _bootSector[offset] = b;

                // Update corresponding ASCII box
                char c = (b >= 32 && b <= 126) ? (char)b : '.';
                _asciiBoxes[offset].Text = c.ToString();

                if (b < 32 || b > 126)
                {
                    _asciiBoxes[offset].Foreground = Brushes.Gray;
                }
                else
                {
                    _asciiBoxes[offset].Foreground = Brushes.White;
                }
            }
            else
            {
                // Revert to original
                box.Text = _bootSector[offset].ToString("X2");
            }
        }

        private void AsciiBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box) return;

            box.Background = Brushes.Transparent;

            int offset = GetOffset(box, _asciiBoxes);
            if (offset < 0 || offset >= 512) return;

            // Always revert the text box to whatever _bootSector currently holds.
            // This prevents navigation from converting non-printable placeholder dots ('.') into 0x2E.
            byte originalByte = _bootSector[offset];
            char originalChar = (originalByte >= 32 && originalByte <= 126) ? (char)originalByte : '.';
            box.Text = originalChar.ToString();
            
            if (originalByte < 32 || originalByte > 126)
            {
                box.Foreground = Brushes.Gray;
            }
            else
            {
                box.Foreground = Brushes.White;
            }
        }

        private void MoveFocus(TextBox currentBox, int direction, TextBox[] boxes)
        {
            int currentOffset = GetOffset(currentBox, boxes);
            int newOffset = currentOffset + direction;

            if (newOffset >= 0 && newOffset < 512)
            {
                boxes[newOffset].Focus();
                boxes[newOffset].SelectAll();
            }
        }

        private int GetOffset(TextBox box, TextBox[] boxes)
        {
            for (int i = 0; i < boxes.Length; i++)
            {
                if (boxes[i] == box)
                    return i;
            }
            return -1;
        }

        private void MoveCaretToEnd(TextBox box)
        {
            box.SelectionStart = box.Text.Length;
            box.SelectionLength = 0;
        }

        private bool IsValidHex(string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;

            if (text.Length > 2)
                return false;

            foreach (char c in text)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                    return false;
            }
            return true;
        }

        private void LoadDataButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Binary Files|*.bin|All Files|*.*",
                DefaultExt = ".bin",
                Title = "Load Boot Sector"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    byte[] loadedData = System.IO.File.ReadAllBytes(openFileDialog.FileName);
                    if (loadedData.Length != 512)
                    {
                        MessageBox.Show("File must be exactly 512 bytes.", "Invalid File", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    SetBootSector(loadedData);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SaveDataButton_Click(object sender, RoutedEventArgs e)
        {
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "Binary Files|*.bin|All Files|*.*",
                DefaultExt = ".bin",
                FileName = "bootsector.bin",
                Title = "Save Boot Sector"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    System.IO.File.WriteAllBytes(saveFileDialog.FileName, _bootSector);
                    MessageBox.Show("Boot sector saved successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error saving file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            EditedBootSector = (byte[])_bootSector.Clone();
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            EditedBootSector = null;
            DialogResult = false;
            Close();
        }
    }
}
