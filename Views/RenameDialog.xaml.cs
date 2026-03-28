using System.Windows;

namespace NetImage.Views
{
    public partial class RenameDialog : Window
    {
        public string NewName { get; private set; } = string.Empty;

        public RenameDialog()
        {
            InitializeComponent();
            Loaded += (s, e) => NameTextBox.Focus();
        }

        public void SetItemInfo(string currentName, bool isFolder)
        {
            NameTextBox.Text = currentName;
            PromptTextBlock.Text = $"Enter new {(isFolder ? "folder" : "file")} name:";
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            NewName = NameTextBox.Text.Trim();
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
