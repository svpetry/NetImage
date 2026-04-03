using System.Text;
using System.Windows;
using System.Windows.Input;

namespace NetImage.Views
{
    public partial class FileEditDialog : Window
    {
        private Encoding _encoding = Encoding.GetEncoding("IBM437") ?? Encoding.ASCII;
        private string _originalText = string.Empty;
        private string _lastSearchText = string.Empty;
        private bool _lastMatchCase = false;
        public string FileName { get; private set; } = string.Empty;
        public long? FileSize { get; private set; }
        public DateTime? ModifiedTime { get; private set; }
        public byte[]? EditedContent { get; private set; }

        public FileEditDialog()
        {
            InitializeComponent();
            DataContext = this;
        }

        public void SetFileContent(string fileName, byte[] content, long? fileSize = null, DateTime? modifiedTime = null)
        {
            FileName = fileName;
            FileSize = fileSize;
            ModifiedTime = modifiedTime;

            try
            {
                EditTextBox.Text = _encoding.GetString(content);
            }
            catch
            {
                EditTextBox.Text = "[Binary file - cannot display as text]";
            }
            
            _originalText = EditTextBox.Text;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EditedContent = _encoding.GetBytes(EditTextBox.Text);
                _originalText = EditTextBox.Text;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                EditedContent = null;
                MessageBox.Show(this, "Error saving: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (EditTextBox.Text != _originalText)
            {
                var result = MessageBox.Show(this, "You have unsaved changes. Do you want to save them?", "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                
                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                }
                else if (result == MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    Dispatcher.InvokeAsync(() => SaveButton_Click(this, new RoutedEventArgs()));
                }
                else if (result == MessageBoxResult.No)
                {
                    EditedContent = null;
                }
            }
            else if (DialogResult != true)
            {
                EditedContent = null;
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                OpenFindDialog();
                e.Handled = true;
            }
            else if (e.Key == Key.F3)
            {
                FindNext(fromStart: false);
                e.Handled = true;
            }
        }

        private void MenuFind_Click(object sender, RoutedEventArgs e)
        {
            OpenFindDialog();
        }

        private void EditTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            int lineIndex = EditTextBox.GetLineIndexFromCharacterIndex(EditTextBox.SelectionStart);
            int column = EditTextBox.SelectionStart - EditTextBox.GetCharacterIndexFromLineIndex(lineIndex);
            RowTextBlock.Text = (lineIndex + 1).ToString();
            ColumnTextBlock.Text = (column + 1).ToString();
        }

        private void OpenFindDialog()
        {
            var dialog = new FindDialog(_lastSearchText, _lastMatchCase)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                _lastSearchText = dialog.SearchText;
                _lastMatchCase = dialog.MatchCase;
                FindNext(fromStart: false);
            }
        }

        private void FindNext(bool fromStart)
        {
            if (string.IsNullOrEmpty(_lastSearchText))
                return;

            string text = EditTextBox.Text;
            int startIndex = fromStart ? 0 : EditTextBox.SelectionStart + EditTextBox.SelectionLength;

            StringComparison comparison = _lastMatchCase ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;

            int index = text.IndexOf(_lastSearchText, startIndex, comparison);

            if (index == -1 && startIndex > 0)
            {
                MessageBoxResult result = MessageBox.Show(this, $"Cannot find \"{_lastSearchText}\". Search from beginning?", "Find", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (result == MessageBoxResult.Yes)
                {
                    index = text.IndexOf(_lastSearchText, 0, comparison);
                }
            }

            if (index != -1)
            {
                EditTextBox.Focus();
                EditTextBox.Select(index, _lastSearchText.Length);
                int lineIndex = EditTextBox.GetLineIndexFromCharacterIndex(index);
                if (lineIndex >= 0)
                {
                    EditTextBox.ScrollToLine(lineIndex);
                }
            }
            else
            {
                MessageBox.Show(this, $"Cannot find \"{_lastSearchText}\".", "Find", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
