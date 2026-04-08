using System;

namespace NetImage.ViewModels
{
    public class AboutRequestEventArgs : EventArgs
    {
        public AboutRequestEventArgs(string applicationName, string version, string repositoryUrl, string releaseDate)
        {
            ApplicationName = applicationName;
            Version = version;
            RepositoryUrl = repositoryUrl;
            ReleaseDate = releaseDate;
        }

        public string ApplicationName { get; }
        public string Version { get; }
        public string RepositoryUrl { get; }
        public string ReleaseDate { get; }
    }
}
