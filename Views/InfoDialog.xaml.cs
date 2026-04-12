using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NetImage.Views
{
    public partial class InfoDialog : Window
    {
        public ImageSource InfoIcon { get; }

        public InfoDialog(string message, string title)
        {
            InitializeComponent();
            Title = title;
            MessageText.Text = message;
            InfoIcon = Imaging.CreateBitmapSourceFromHIcon(SystemIcons.Information.Handle, Int32Rect.Empty, null);
            DataContext = this;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
