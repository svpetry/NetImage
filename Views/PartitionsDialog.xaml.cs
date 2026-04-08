using System.Collections.Generic;
using System.Windows;
using NetImage.Models;

namespace NetImage.Views
{
    public partial class PartitionsDialog : Window
    {
        public PartitionsDialog(IReadOnlyList<MbrPartition> partitions, uint bytesPerSector)
        {
            InitializeComponent();
            DataContext = partitions;
            SectorSizeText.Text = $"Sector size: {bytesPerSector} bytes";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
