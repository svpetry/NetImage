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
                await FetchReleaseDateAsync(version);
            };
        }

        private async Task FetchReleaseDateAsync(string version)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "NetImage-App");

                var tagsResponse = await client.GetAsync("https://api.github.com/repos/svpetry/NetImage/tags");
                if (!tagsResponse.IsSuccessStatusCode) return;

                var tagsJson = await tagsResponse.Content.ReadAsStringAsync();
                using var tagsDoc = JsonDocument.Parse(tagsJson);

                Version? currentVersion = null;
                Version? latestVersion = null;
                string? latestVersionTag = null;

                foreach (var tag in tagsDoc.RootElement.EnumerateArray())
                {
                    var tagName = tag.GetProperty("name").GetString();
                    if (tagName == null || !tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var tagVersionStr = tagName.Substring(1);
                    if (!Version.TryParse(tagVersionStr, out var tagVersion))
                        continue;

                    if (tagName.Equals($"v{version}", StringComparison.OrdinalIgnoreCase))
                    {
                        currentVersion = tagVersion;
                    }

                    if (latestVersion == null || tagVersion > latestVersion)
                    {
                        latestVersion = tagVersion;
                        latestVersionTag = tagName;
                    }
                }

                if (currentVersion != null && latestVersion != null && latestVersion > currentVersion)
                {
                    Dispatcher.Invoke(() =>
                    {
                        NewVersionTextBlock.Visibility = Visibility.Visible;
                        NewVersionRun.Text = latestVersionTag;
                    });
                }
                else if (currentVersion != null)
                {
                    var commitUrl = "";
                    foreach (var tag in tagsDoc.RootElement.EnumerateArray())
                    {
                        var tagName = tag.GetProperty("name").GetString();
                        if (tagName != null && tagName.Equals($"v{version}", StringComparison.OrdinalIgnoreCase))
                        {
                            commitUrl = tag.GetProperty("commit").GetProperty("url").GetString() ?? "";
                            break;
                        }
                    }

                    if (!string.IsNullOrEmpty(commitUrl))
                    {
                        var commitResponse = await client.GetAsync(commitUrl);
                        if (commitResponse.IsSuccessStatusCode)
                        {
                            var commitJson = await commitResponse.Content.ReadAsStringAsync();
                            using var commitDoc = JsonDocument.Parse(commitJson);
                            var dateStr = commitDoc.RootElement.GetProperty("commit").GetProperty("author").GetProperty("date").GetString();
                            if (!string.IsNullOrEmpty(dateStr))
                            {
                                Dispatcher.Invoke(() => ReleaseDateRun.Text = dateStr.Split('T')[0]);
                            }
                        }
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
