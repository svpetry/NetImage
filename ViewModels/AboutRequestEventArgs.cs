using System;

namespace NetImage.ViewModels
{
    public class AboutRequestEventArgs : EventArgs
    {
        public AboutRequestEventArgs(string applicationName, string version, string repositoryUrl)
        {
            ApplicationName = applicationName;
            Version = version;
            RepositoryUrl = repositoryUrl;
        }

        public string ApplicationName { get; }
        public string Version { get; }
        public string RepositoryUrl { get; }
    }
}
