using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace NetImage.Views
{
    public partial class AboutDialog : Window
    {
        public AboutDialog(string applicationName, string version, string repositoryUrl)
        {
            InitializeComponent();

            ApplicationNameTextBlock.Text = applicationName;
            VersionRun.Text = version;
            RepositoryLinkRun.Text = repositoryUrl;
            RepositoryHyperlink.NavigateUri = new Uri(repositoryUrl);
        }

        private void RepositoryHyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open link:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            e.Handled = true;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
