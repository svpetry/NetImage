using System.Windows;

namespace NetImage.Views
{
    public partial class FindDialog : Window
    {
        public string SearchText => SearchTextBox.Text;
        public bool MatchCase => MatchCaseCheckBox.IsChecked == true;

        public FindDialog(string initialSearchText = "", bool initialMatchCase = false)
        {
            InitializeComponent();
            SearchTextBox.Text = initialSearchText;
            MatchCaseCheckBox.IsChecked = initialMatchCase;
            SearchTextBox.SelectAll();
            SearchTextBox.Focus();
        }

        private void FindNext_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
