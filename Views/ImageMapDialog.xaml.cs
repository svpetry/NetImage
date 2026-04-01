using System;
using System.Text;
using System.Windows;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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

            var typesCount = new Dictionary<DiskImageWorker.SectorType, int>();

            for (int i = 0; i < sectorsToShow; i++)
            {
                var sectorType = sectorMap.SectorTypes[i];
                
                typesCount.TryGetValue(sectorType, out int count);
                typesCount[sectorType] = count + 1;

                var block = CreateSectorBlock(sectorType);
                SectorGrid.Children.Add(block);
            }

            // Update info text
            UpdateInfoTextSector(sectorMap);

            // Update statistics
            UpdateStatisticsSector(sectorMap);

            // Show sector legend items
            UpdateLegendVisibility(true);

            // Calculate exact total distribution for pie chart (don't limit to MaxSectorsToShow)
            var realTypesCount = new Dictionary<DiskImageWorker.SectorType, int>();
            
            // Extract metadata sectors count from the partial map (these are all at the beginning anyway)
            for (int i = 0; i < sectorsToShow; i++)
            {
                var sectorType = sectorMap.SectorTypes[i];
                if (sectorType != DiskImageWorker.SectorType.Allocated && 
                    sectorType != DiskImageWorker.SectorType.Free && 
                    sectorType != DiskImageWorker.SectorType.Reserved)
                {
                    realTypesCount.TryGetValue(sectorType, out int count);
                    realTypesCount[sectorType] = count + 1;
                }
            }

            // Use exact totals for the bulk regions
            realTypesCount[DiskImageWorker.SectorType.Allocated] = sectorMap.TotalAllocatedSectors;
            realTypesCount[DiskImageWorker.SectorType.Free] = sectorMap.TotalFreeSectors;

            // Any remaining uncounted space falls into the reserved/other category
            int totalCounted = 0;
            foreach (var value in realTypesCount.Values)
            {
                totalCounted += value;
            }
            int reservedSpace = sectorMap.TotalSectors - totalCounted;
            if (reservedSpace > 0)
            {
                realTypesCount[DiskImageWorker.SectorType.Reserved] = reservedSpace;
            }

            // Draw pie chart over the whole total disk layout
            DrawPieChart(realTypesCount, sectorMap.TotalSectors);
        }

        private void DisplayClusterMap()
        {
            if (_worker == null || _imageData == null) return;

            SectorGrid.Children.Clear();

            var clusterMap = _worker.GetClusterMap();
            var clustersToShow = Math.Min(clusterMap.TotalClusters, clusterMap.MaxClustersToShow);

            // Create uniform grid for clusters
            SectorGrid.Columns = BlocksPerRow;

            var typesCount = new Dictionary<DiskImageWorker.SectorType, int>();

            for (int i = 0; i < clustersToShow; i++)
            {
                var clusterType = clusterMap.ClusterTypes[i];

                typesCount.TryGetValue(clusterType, out int count);
                typesCount[clusterType] = count + 1;

                var block = CreateClusterBlock(clusterType);
                SectorGrid.Children.Add(block);
            }

            // Update info text
            UpdateInfoTextCluster(clusterMap);

            // Update statistics
            UpdateStatisticsCluster(clusterMap);

            // Show cluster legend items
            UpdateLegendVisibility(false);

            // Draw pie chart using the true total distribution map
            var realTypesCount = new Dictionary<DiskImageWorker.SectorType, int>();
            realTypesCount[DiskImageWorker.SectorType.Allocated] = clusterMap.TotalAllocatedClusters;
            realTypesCount[DiskImageWorker.SectorType.Free] = clusterMap.TotalFreeClusters;

            DrawPieChart(realTypesCount, clusterMap.TotalClusters);
        }

        private Border CreateSectorBlock(DiskImageWorker.SectorType type)
        {
            return new Border
            {
                Width = BlockWidth,
                Height = BlockHeight,
                Margin = new Thickness(1),
                BorderThickness = new Thickness(0),
                Background = GetColorForSectorType(type)
            };
        }

        private Border CreateClusterBlock(DiskImageWorker.SectorType type)
        {
            return new Border
            {
                Width = BlockWidth,
                Height = BlockHeight,
                Margin = new Thickness(1),
                BorderThickness = new Thickness(0),
                Background = GetColorForSectorType(type)
            };
        }

        private Brush GetColorForSectorType(DiskImageWorker.SectorType type)
        {
            switch (type)
            {
                case DiskImageWorker.SectorType.BootSector:
                    return new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                case DiskImageWorker.SectorType.PartitionTable:
                    return new SolidColorBrush(Color.FromRgb(33, 150, 243)); // Blue
                case DiskImageWorker.SectorType.FatTable:
                    return new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange
                case DiskImageWorker.SectorType.RootDirectory:
                    return new SolidColorBrush(Color.FromRgb(156, 39, 176)); // Purple
                case DiskImageWorker.SectorType.Allocated:
                    return new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red
                case DiskImageWorker.SectorType.Free:
                    return new SolidColorBrush(Color.FromRgb(158, 158, 158)); // Gray
                case DiskImageWorker.SectorType.Reserved:
                default:
                    return new SolidColorBrush(Color.FromRgb(96, 125, 139)); // Blue-gray
            }
        }

        private void DrawPieChart(Dictionary<DiskImageWorker.SectorType, int> counts, int totalItems)
        {
            PieChartCanvas.Children.Clear();
            if (totalItems <= 0) return;

            double radius = Math.Min(PieChartCanvas.Width, PieChartCanvas.Height) / 2;
            double center = radius; // Assuming square Canvas Width=Height
            
            double currentAngle = 0;

            foreach (var kvp in counts)
            {
                if (kvp.Value == 0) continue;

                double slicePercentage = (double)kvp.Value / totalItems;
                double sweepAngle = slicePercentage * 360;

                Brush fillBrush = GetColorForSectorType(kvp.Key);

                if (slicePercentage >= 0.9999)
                {
                    // Draw full circle
                    var ellipse = new Ellipse
                    {
                        Width = radius * 2,
                        Height = radius * 2,
                        Fill = fillBrush
                    };
                    PieChartCanvas.Children.Add(ellipse);
                    continue;
                }

                // Standard pie chart math:
                // 0 degrees is typically at top (Y=0), clockwise increasing
                double startAngleRad = (currentAngle - 90) * Math.PI / 180.0;
                double endAngleRad = (currentAngle + sweepAngle - 90) * Math.PI / 180.0;

                Point startPoint = new Point(center + radius * Math.Cos(startAngleRad), center + radius * Math.Sin(startAngleRad));
                Point endPoint = new Point(center + radius * Math.Cos(endAngleRad), center + radius * Math.Sin(endAngleRad));

                bool isLargeArc = sweepAngle > 180.0;

                var pathFigure = new PathFigure
                {
                    StartPoint = new Point(center, center),
                    IsClosed = true
                };

                pathFigure.Segments.Add(new LineSegment(startPoint, false));
                pathFigure.Segments.Add(new ArcSegment(endPoint, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, false));

                var pathGeometry = new PathGeometry();
                pathGeometry.Figures.Add(pathFigure);

                var path = new Path
                {
                    Fill = fillBrush,
                    Data = pathGeometry
                };

                PieChartCanvas.Children.Add(path);

                currentAngle += sweepAngle;
            }
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
