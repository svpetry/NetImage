using System.Text;
using System.Windows;

namespace NetImage.Views
{
    public partial class FileEditDialog : Window
    {
        public string FileName { get; private set; } = string.Empty;
        public byte[]? EditedContent { get; private set; }

        public FileEditDialog()
        {
            InitializeComponent();
        }

        public void SetFileContent(string fileName, byte[] content)
        {
            FileName = fileName;

            try
            {
                EditTextBox.Text = Encoding.ASCII.GetString(content);
            }
            catch
            {
                EditTextBox.Text = "[Binary file - cannot display as text]";
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EditedContent = Encoding.ASCII.GetBytes(EditTextBox.Text);
                DialogResult = true;
                Close();
            }
            catch
            {
                EditedContent = null;
                DialogResult = false;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            EditedContent = null;
            DialogResult = false;
            Close();
        }
    }
}
