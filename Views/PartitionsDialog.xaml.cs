using System.Collections.Generic;
using System.Windows;
using NetImage.Models;

namespace NetImage.Views
{
    public partial class PartitionsDialog : Window
    {
        public PartitionsDialog(IReadOnlyList<MbrPartition> partitions)
        {
            InitializeComponent();
            DataContext = partitions;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
