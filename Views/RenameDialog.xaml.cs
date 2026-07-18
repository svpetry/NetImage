using System.Windows;

namespace NetImage.Views
{
    public partial class RenameDialog : Window
    {
        private string _originalName = string.Empty;

        public string NewName { get; private set; } = string.Empty;

        public RenameDialog()
        {
            InitializeComponent();
            Loaded += (s, e) => NameTextBox.Focus();
        }

        public void SetItemInfo(string currentName, bool isFolder, bool isVolume = false)
        {
            _originalName = currentName;
            NameTextBox.Text = currentName;
            if (isVolume)
                PromptTextBlock.Text = "Enter new volume label:";
            else
                PromptTextBlock.Text = $"Enter new {(isFolder ? "folder" : "file")} name:";
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            NewName = NameTextBox.Text.Trim();
            if (NewName == _originalName)
            {
                DialogResult = false;
                Close();
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
