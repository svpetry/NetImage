using System.Text;
using System.Windows;

namespace NetImage.Views
{
    public partial class FileEditDialog : Window
    {
        private Encoding _encoding = Encoding.GetEncoding("IBM437") ?? Encoding.ASCII;
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
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EditedContent = _encoding.GetBytes(EditTextBox.Text);
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
