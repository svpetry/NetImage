using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetImage.Workers;

namespace NetImage.Views
{
    public partial class ImageMapDialog : Window
    {
        private const int BlockWidth = 12;
        private const int BlockHeight = 12;
        private const int BlocksPerRow = 50;

        private DiskImageWorker? _worker;
        private byte[]? _imageData;

        public ImageMapDialog()
        {
            InitializeComponent();
            DataContext = this;
        }

        public void LoadImage(DiskImageWorker worker)
        {
            _worker = worker;
            _imageData = worker.GetImageData();
            if (_imageData == null)
            {
                MessageBox.Show("No image data available.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Default to sector view
            DisplaySectorMap();
        }

        private void DisplaySectorMap()
        {
            if (_worker == null || _imageData == null) return;

            SectorGrid.Children.Clear();

            var sectorMap = _worker.GetSectorMap();
            var sectorsToShow = Math.Min(sectorMap.TotalSectors, sectorMap.MaxSectorsToShow);

            // Create uniform grid for sectors
            SectorGrid.Columns = BlocksPerRow;

            for (int i = 0; i < sectorsToShow; i++)
            {
                var sectorType = sectorMap.SectorTypes[i];
                var block = CreateSectorBlock(sectorType);
                SectorGrid.Children.Add(block);
            }

            // Update info text
            UpdateInfoTextSector(sectorMap);

            // Update statistics
            UpdateStatisticsSector(sectorMap);

            // Show sector legend items
            UpdateLegendVisibility(true);
        }

        private void DisplayClusterMap()
        {
            if (_worker == null || _imageData == null) return;

            SectorGrid.Children.Clear();

            var clusterMap = _worker.GetClusterMap();
            var clustersToShow = Math.Min(clusterMap.TotalClusters, clusterMap.MaxClustersToShow);

            // Create uniform grid for clusters
            SectorGrid.Columns = BlocksPerRow;

            for (int i = 0; i < clustersToShow; i++)
            {
                var clusterType = clusterMap.ClusterTypes[i];
                var block = CreateClusterBlock(clusterType);
                SectorGrid.Children.Add(block);
            }

            // Update info text
            UpdateInfoTextCluster(clusterMap);

            // Update statistics
            UpdateStatisticsCluster(clusterMap);

            // Show cluster legend items
            UpdateLegendVisibility(false);
        }

        private Border CreateSectorBlock(DiskImageWorker.SectorType type)
        {
            var border = new Border
            {
                Width = BlockWidth,
                Height = BlockHeight,
                Margin = new Thickness(1),
                BorderThickness = new Thickness(0),
            };

            switch (type)
            {
                case DiskImageWorker.SectorType.BootSector:
                    border.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                    break;
                case DiskImageWorker.SectorType.PartitionTable:
                    border.Background = new SolidColorBrush(Color.FromRgb(33, 150, 243)); // Blue
                    break;
                case DiskImageWorker.SectorType.FatTable:
                    border.Background = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange
                    break;
                case DiskImageWorker.SectorType.RootDirectory:
                    border.Background = new SolidColorBrush(Color.FromRgb(156, 39, 176)); // Purple
                    break;
                case DiskImageWorker.SectorType.Allocated:
                    border.Background = new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red
                    break;
                case DiskImageWorker.SectorType.Free:
                    border.Background = new SolidColorBrush(Color.FromRgb(158, 158, 158)); // Gray
                    break;
                case DiskImageWorker.SectorType.Reserved:
                default:
                    border.Background = new SolidColorBrush(Color.FromRgb(96, 125, 139)); // Blue-gray
                    break;
            }

            return border;
        }

        private Border CreateClusterBlock(DiskImageWorker.SectorType type)
        {
            var border = new Border
            {
                Width = BlockWidth,
                Height = BlockHeight,
                Margin = new Thickness(1),
                BorderThickness = new Thickness(0),
            };

            switch (type)
            {
                case DiskImageWorker.SectorType.Allocated:
                    border.Background = new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red
                    break;
                case DiskImageWorker.SectorType.Free:
                default:
                    border.Background = new SolidColorBrush(Color.FromRgb(158, 158, 158)); // Gray
                    break;
            }

            return border;
        }

        private void UpdateInfoTextSector(DiskImageWorker.SectorMapInfo sectorMap)
        {
            var info = new System.Text.StringBuilder();
            info.AppendLine($"Total Sectors: {sectorMap.TotalSectors:N0}");

            if (sectorMap.HasPartition)
            {
                info.AppendLine($"Partition Start: Sector {sectorMap.PartitionStartSector:N0}");
            }

            if (sectorMap.BytesPerSector > 0)
            {
                info.AppendLine($"Bytes Per Sector: {sectorMap.BytesPerSector}");
            }

            if (sectorMap.SectorsPerCluster > 0)
            {
                info.AppendLine($"Sectors Per Cluster: {sectorMap.SectorsPerCluster}");
            }

            InfoTextBlock.Text = info.ToString().TrimEnd();
        }

        private void UpdateInfoTextCluster(DiskImageWorker.ClusterMapInfo clusterMap)
        {
            var info = new System.Text.StringBuilder();
            info.AppendLine($"Total Clusters: {clusterMap.TotalClusters:N0}");
            info.AppendLine($"Total Sectors: {clusterMap.TotalSectors:N0}");

            if (clusterMap.HasPartition)
            {
                info.AppendLine($"Partition Start: Sector {clusterMap.PartitionStartSector:N0}");
            }

            if (clusterMap.BytesPerSector > 0)
            {
                info.AppendLine($"Bytes Per Sector: {clusterMap.BytesPerSector}");
            }

            if (clusterMap.SectorsPerCluster > 0)
            {
                info.AppendLine($"Sectors Per Cluster: {clusterMap.SectorsPerCluster}");
            }

            InfoTextBlock.Text = info.ToString().TrimEnd();
        }

        private void UpdateStatisticsSector(DiskImageWorker.SectorMapInfo sectorMap)
        {
            var totalSize = FormatBytes(_imageData!.LongLength);
            var allocatedSize = FormatBytes((long)sectorMap.TotalAllocatedSectors * sectorMap.BytesPerSector);
            var freeSize = FormatBytes((long)sectorMap.TotalFreeSectors * sectorMap.BytesPerSector);

            var usagePercent = sectorMap.TotalSectors > 0
                ? ((double)sectorMap.TotalAllocatedSectors / sectorMap.TotalSectors * 100).ToString("F1")
                : "0.0";

            TotalSizeText.Text = totalSize;
            AllocatedText.Text = allocatedSize;
            FreeText.Text = freeSize;
            UsageText.Text = $"{usagePercent}%";
        }

        private void UpdateStatisticsCluster(DiskImageWorker.ClusterMapInfo clusterMap)
        {
            var bytesPerCluster = clusterMap.BytesPerSector * clusterMap.SectorsPerCluster;
            var totalSize = FormatBytes(clusterMap.TotalClusters * bytesPerCluster);
            var allocatedSize = FormatBytes(clusterMap.TotalAllocatedClusters * bytesPerCluster);
            var freeSize = FormatBytes(clusterMap.TotalFreeClusters * bytesPerCluster);

            var usagePercent = clusterMap.TotalClusters > 0
                ? ((double)clusterMap.TotalAllocatedClusters / clusterMap.TotalClusters * 100).ToString("F1")
                : "0.0";

            TotalSizeText.Text = totalSize;
            AllocatedText.Text = allocatedSize;
            FreeText.Text = freeSize;
            UsageText.Text = $"{usagePercent}%";
        }

        private string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        private void UpdateLegendVisibility(bool isSectorView)
        {
            // The legend items will be shown/hidden based on view mode
            // Sector view shows all types, cluster view only shows Allocated/Free
        }

        private void ViewMode_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radioButton)
            {
                if (radioButton.Content?.ToString() == "Clusters")
                {
                    DisplayClusterMap();
                }
                else
                {
                    DisplaySectorMap();
                }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
