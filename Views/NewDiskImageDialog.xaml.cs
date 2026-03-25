using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace NetImage.Views
{
    public partial class NewDiskImageDialog : Window
    {
        public long SelectedSize { get; private set; }

        public NewDiskImageDialog()
        {
            InitializeComponent();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var stackPanel = MainGrid.Children.OfType<StackPanel>().FirstOrDefault();
            if (stackPanel != null)
            {
                foreach (RadioButton radio in stackPanel.Children.OfType<RadioButton>())
                {
                    if (radio.IsChecked == true)
                    {
                        if (long.TryParse(radio.Tag?.ToString(), out long size))
                        {
                            SelectedSize = size;
                            break;
                        }
                    }
                }
            }

            if (SelectedSize > 0)
            {
                DialogResult = true;
                Close();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
