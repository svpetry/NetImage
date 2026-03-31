using System.Configuration;
using System.Data;
using System.Windows;

namespace NetImage
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static string? CommandLineFilePath { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Handle command-line arguments (file paths)
            if (e.Args.Length > 0)
            {
                CommandLineFilePath = e.Args[0];
            }
        }
    }

}
