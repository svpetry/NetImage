using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
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
            ReleaseDateRun.Text = "...";
            RepositoryLinkRun.Text = repositoryUrl;
            RepositoryHyperlink.NavigateUri = new Uri(repositoryUrl);

            Loaded += async (s, e) =>
            {
                await FetchReleaseDateAsync();
            };
        }

        private async Task FetchReleaseDateAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "NetImage-App");

                var tagsResponse = await client.GetAsync("https://api.github.com/repos/svpetry/NetImage/tags");
                if (!tagsResponse.IsSuccessStatusCode) return;

                var tagsJson = await tagsResponse.Content.ReadAsStringAsync();
                using var tagsDoc = JsonDocument.Parse(tagsJson);

                foreach (var tag in tagsDoc.RootElement.EnumerateArray())
                {
                    var commitUrl = tag.GetProperty("commit").GetProperty("url").GetString();
                    if (string.IsNullOrEmpty(commitUrl)) continue;

                    var commitResponse = await client.GetAsync(commitUrl);
                    if (!commitResponse.IsSuccessStatusCode) continue;

                    var commitJson = await commitResponse.Content.ReadAsStringAsync();
                    using var commitDoc = JsonDocument.Parse(commitJson);
                    var dateStr = commitDoc.RootElement.GetProperty("commit").GetProperty("author").GetProperty("date").GetString();
                    if (!string.IsNullOrEmpty(dateStr))
                    {
                        Dispatcher.Invoke(() => ReleaseDateRun.Text = dateStr.Split('T')[0]);
                        return;
                    }
                }
            }
            catch { }
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
