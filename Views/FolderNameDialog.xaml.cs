using System.Windows;

namespace NetImage.Views
{
    public partial class FolderNameDialog : Window
    {
        public string FolderName { get; private set; } = string.Empty;

        public FolderNameDialog()
        {
            InitializeComponent();
            Loaded += (s, e) => FolderNameTextBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            FolderName = FolderNameTextBox.Text.Trim();
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
