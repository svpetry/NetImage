using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace NetImage.Workers
{
    public record FileEntry(string Path, long? Size, DateTime? Modified);

    public class DiskImageWorker
    {
        private byte[]? _imageData;
        private bool _isLoaded;

        public event EventHandler? LoadingStarted;
        public event EventHandler? LoadingCompleted;

        public DiskImageWorker(string filePath)
        {
            FilePath = filePath;
            FilesAndFolders = new List<FileEntry>();
        }

        public string FilePath { get; set; }

        public string VolumeLabel { get; private set; } = string.Empty;

        public List<FileEntry> FilesAndFolders { get; private set; }

        public bool IsLoaded
        {
            get => _isLoaded;
        }

        public async Task OpenAsync()
        {
            if (_isLoaded)
            {
                return;
            }

            LoadingStarted?.Invoke(this, EventArgs.Empty);

            _imageData = await File.ReadAllBytesAsync(FilePath);
            ParseFatFilesystem();
            _isLoaded = true;

            LoadingCompleted?.Invoke(this, EventArgs.Empty);
        }

        public async Task SaveAsync(string imageName)
        {
            if (_imageData == null)
                throw new InvalidOperationException("No image data to save.");

            await File.WriteAllBytesAsync(imageName, _imageData);
        }

        private void ParseFatFilesystem()
        {
            FilesAndFolders.Clear();
            VolumeLabel = string.Empty;

            if (_imageData == null || _imageData.Length < 512)
            {
                return;
            }

            var bootSector = new ReadOnlySpan<byte>(_imageData, 0, 512);

            if (!IsFatBootSector(bootSector))
            {
                return;
            }

            var bpb = ParseBpb(bootSector);

            var rootDirStart = bpb.ReservedSectors + (bpb.NumFats * bpb.SectorsPerFat);

            var rootDirEntries = bpb.RootDirEntries > 0 ? bpb.RootDirEntries : 0;
            var rootDirSizeSectors = (int)(((rootDirEntries * 32) + (bpb.BytesPerSector - 1)) / bpb.BytesPerSector);

            ExtractVolumeLabel(rootDirStart, rootDirSizeSectors, bpb);

            if (string.IsNullOrWhiteSpace(VolumeLabel))
            {
                VolumeLabel = System.IO.Path.GetFileNameWithoutExtension(FilePath);
            }

            ReadDirectoryEntries(rootDirStart, rootDirSizeSectors, string.Empty, bpb);
        }

        private void ExtractVolumeLabel(uint startSector, int numSectors, Bpb bpb)
        {
            var imageData = _imageData!;
            var entryCount = numSectors * 512 / 32;

            for (int i = 0; i < entryCount; i++)
            {
                var offset = (int)(startSector * 512) + (i * 32);

                if (offset + 32 > imageData.Length)
                    break;

                var entry = new ReadOnlySpan<byte>(imageData, offset, 32);
                var firstByte = entry[0];

                if (firstByte == 0x00 || firstByte == 0xE5)
                    continue;

                const byte ATTR_VOLUME_LABEL = 0x08;
                if ((entry[11] & ATTR_VOLUME_LABEL) != 0)
                {
                    var labelBytes = entry.Slice(0, 11).ToArray();
                    var namePart = System.Text.Encoding.ASCII.GetString(labelBytes).TrimEnd();
                    if (!string.IsNullOrWhiteSpace(namePart))
                    {
                        VolumeLabel = namePart;
                        return;
                    }
                }
            }
        }

        private bool IsFatBootSector(ReadOnlySpan<byte> sector)
        {
            if (sector.Length < 510)
                return false;

            return sector[510] == 0x55 && sector[511] == 0xAA;
        }

        private bool IsFat12(Bpb bpb)
        {
            return bpb.TotalSectors16 <= 4085 || (bpb.TotalSectors32 > 0 && bpb.TotalSectors32 <= 4085);
        }

        private Bpb ParseBpb(ReadOnlySpan<byte> bootSector)
        {
            var bpb = new Bpb
            {
                BytesPerSector = BitConverter.ToUInt16(bootSector.Slice(11, 2)),
                SectorsPerCluster = bootSector[13],
                ReservedSectors = BitConverter.ToUInt16(bootSector.Slice(14, 2)),
                NumFats = bootSector[16],
                RootDirEntries = BitConverter.ToUInt16(bootSector.Slice(17, 2)),
                TotalSectors16 = BitConverter.ToUInt16(bootSector.Slice(19, 2)),
                MediaDescriptor = bootSector[21],
                SectorsPerFat16 = BitConverter.ToUInt16(bootSector.Slice(22, 2)),
                SectorsPerTrack = BitConverter.ToUInt16(bootSector.Slice(24, 2)),
                HeadsPerCylinder = BitConverter.ToUInt16(bootSector.Slice(26, 2)),
                HiddenSectors = BitConverter.ToUInt32(bootSector.Slice(28, 4)),
                TotalSectors32 = BitConverter.ToUInt32(bootSector.Slice(32, 4))
            };

            bpb.SectorsPerFat = bpb.SectorsPerFat16 == 0 ? bpb.TotalSectors32 : (uint)bpb.SectorsPerFat16;

            return bpb;
        }

        private void ReadDirectoryEntries(uint startSector, int numSectors, string parentPath, Bpb bpb)
        {
            var imageData = _imageData!;
            var entryCount = numSectors * 512 / 32;

            for (int i = 0; i < entryCount; i++)
            {
                var offset = (int)(startSector * 512) + (i * 32);

                if (offset + 32 > imageData.Length)
                    break;

                var entry = new ReadOnlySpan<byte>(imageData, offset, 32);
                var firstByte = entry[0];

                // Skip empty entries
                if (firstByte == 0x00)
                    continue;

                // Skip deleted entries (0xE5) and alternate name entries (0x05-0x0A, 0xE5-0xEA)
                if (firstByte == 0xE5 || (firstByte >= 0x05 && firstByte <= 0x0A) || (firstByte >= 0xE5 && firstByte <= 0xEA))
                    continue;

                var name = DecodeEntryName(entry);

                // Skip volume label entries (attribute byte = 0x08)
                const byte ATTR_VOLUME_LABEL = 0x08;
                if ((entry[11] & ATTR_VOLUME_LABEL) != 0)
                    continue;

                // Skip special entries (., ..)
                if (name == "." || name == "..")
                    continue;

                var fullPath = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}\\{name}";
                var isDir = IsDirectoryEntry(entry);
                var fileSize = isDir ? (long?)null : (long)BitConverter.ToUInt32(entry.Slice(28, 4));
                var modified = ParseFatDateTime(BitConverter.ToUInt16(entry.Slice(24, 2)), BitConverter.ToUInt16(entry.Slice(22, 2)));
                FilesAndFolders.Add(new FileEntry(fullPath, fileSize, modified));

                // Recursively traverse subdirectories
                if (isDir)
                {
                    var firstCluster = GetFirstCluster(entry);
                    ReadDirectoryEntriesFromClusterChain(firstCluster, fullPath, bpb);
                }
            }
        }

        private void ReadDirectoryEntriesFromClusterChain(uint firstCluster, string parentPath, Bpb bpb)
        {
            if (firstCluster < 2)
                return;

            var currentCluster = firstCluster;
            var visited = new HashSet<uint>();

            while (currentCluster >= 2 && currentCluster < 0xFF8)
            {
                if (!visited.Add(currentCluster))
                    break; // Cycle guard

                var dirStartSector = ClusterToSector(currentCluster, bpb);
                ReadDirectoryEntries(dirStartSector, bpb.SectorsPerCluster, parentPath, bpb);

                currentCluster = GetNextCluster(currentCluster, bpb);
            }
        }

        private string DecodeEntryName(ReadOnlySpan<byte> entry)
        {
            var namePart1 = DecodeNamePart(entry.Slice(0, 8));
            var extPart = DecodeNamePart(entry.Slice(8, 3));

            var namePart1Trimmed = namePart1.TrimEnd(' ');
            var extPartTrimmed = extPart.TrimEnd(' ');

            var name = string.IsNullOrEmpty(extPartTrimmed)
                ? namePart1Trimmed
                : $"{namePart1Trimmed}.{extPartTrimmed}";

            return name;
        }

        private string DecodeNamePart(ReadOnlySpan<byte> span)
        {
            var result = new System.Text.StringBuilder();
            foreach (var b in span)
            {
                if (b == 0x20)
                    result.Append(' ');
                else if (b >= 0x20 && b < 0x7F)
                    result.Append((char)b);
            }
            return result.ToString();
        }

        private DateTime? ParseFatDateTime(ushort date, ushort time)
        {
            if (date == 0 && time == 0) return null;
            try
            {
                int year = 1980 + (date >> 9);
                int month = (date >> 5) & 0x0F;
                int day = date & 0x1F;
                int hour = time >> 11;
                int minute = (time >> 5) & 0x3F;
                int second = (time & 0x1F) * 2;

                if (month == 0 || day == 0) return null;

                return new DateTime(year, month, day, hour, minute, second);
            }
            catch
            {
                return null;
            }
        }

        private bool IsDirectoryEntry(ReadOnlySpan<byte> entry)
        {
            // Attribute byte is at offset 11
            const byte ATTR_DIRECTORY = 0x10;
            return (entry[11] & ATTR_DIRECTORY) != 0;
        }

        private uint GetFirstCluster(ReadOnlySpan<byte> entry)
        {
            // For FAT12/16, first cluster is stored at offset 26-27 (2 bytes)
            return BitConverter.ToUInt16(entry.Slice(26, 2));
        }

        private uint ClusterToSector(uint cluster, Bpb bpb)
        {
            // FAT starts at cluster 2, so subtract 2
            if (cluster < 2)
                return 0;

            // Calculate the sector where the cluster starts
            // Data area starts after root directory
            var dataStartSector = bpb.ReservedSectors + (bpb.NumFats * bpb.SectorsPerFat) +
                                  ((bpb.RootDirEntries * 32) + (bpb.BytesPerSector - 1)) / bpb.BytesPerSector;

            var sectorsPerCluster = bpb.SectorsPerCluster;
            var clusterOffset = (cluster - 2) * sectorsPerCluster;

            return dataStartSector + clusterOffset;
        }

        public void ExtractFolder(string sourcePath, string hostDestinationPath)
        {
            if (_imageData == null || !_isLoaded)
                throw new InvalidOperationException("Image must be opened before extracting.");

            System.IO.Directory.CreateDirectory(hostDestinationPath);

            var prefix = string.IsNullOrEmpty(sourcePath) ? string.Empty : sourcePath + "\\";

            foreach (var entry in FilesAndFolders)
            {
                if (!string.IsNullOrEmpty(sourcePath) && entry.Path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(prefix) || entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var relativePath = string.IsNullOrEmpty(prefix) ? entry.Path : entry.Path.Substring(prefix.Length);
                    var destFullPath = System.IO.Path.Combine(hostDestinationPath, relativePath);

                    if (entry.Size == null)
                    {
                        System.IO.Directory.CreateDirectory(destFullPath);
                    }
                    else
                    {
                        var content = GetFileContent(entry.Path);
                        if (content != null)
                        {
                            var parentDir = System.IO.Path.GetDirectoryName(destFullPath);
                            if (parentDir != null && !System.IO.Directory.Exists(parentDir))
                                System.IO.Directory.CreateDirectory(parentDir);

                            System.IO.File.WriteAllBytes(destFullPath, content);

                            if (entry.Modified.HasValue)
                            {
                                System.IO.File.SetLastWriteTime(destFullPath, entry.Modified.Value);
                            }
                        }
                    }
                }
            }
        }

        public byte[]? GetFileContent(string path)
        {
            if (_imageData == null || !_isLoaded)
                return null;

            var bootSector = new ReadOnlySpan<byte>(_imageData, 0, 512);
            if (!IsFatBootSector(bootSector))
                return null;

            var bpb = ParseBpb(bootSector);

            var lastSlash = path.LastIndexOf('\\');
            var targetDirectory = lastSlash >= 0 ? path.Substring(0, lastSlash) : string.Empty;
            var name = lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;

            var rootDirStart = bpb.ReservedSectors + (bpb.NumFats * bpb.SectorsPerFat);
            var rootDirEntries = bpb.RootDirEntries > 0 ? bpb.RootDirEntries : 0;
            var rootDirSizeSectors = (int)(((rootDirEntries * 32) + (bpb.BytesPerSector - 1)) / bpb.BytesPerSector);

            uint currentDirSector = rootDirStart;
            int currentDirSectors = rootDirSizeSectors;

            if (!string.IsNullOrEmpty(targetDirectory))
            {
                var parts = targetDirectory.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                bool resolvedFromClusterChain = false;

                foreach (var part in parts)
                {
                    var dirCluster = FindDirectoryCluster(currentDirSector, currentDirSectors, part, resolvedFromClusterChain, bpb);
                    if (dirCluster == 0)
                        return null;

                    currentDirSector = ClusterToSector(dirCluster, bpb);
                    currentDirSectors = bpb.SectorsPerCluster;
                    resolvedFromClusterChain = true;
                }
            }

            return ReadFileContent(currentDirSector, currentDirSectors, name, bpb);
        }

        private byte[]? ReadFileContent(uint startSector, int numSectors, string fileName, Bpb bpb)
        {
            var imageData = _imageData!;
            var entryCount = numSectors * 512 / 32;

            for (int i = 0; i < entryCount; i++)
            {
                var offset = (int)(startSector * 512) + (i * 32);

                if (offset + 32 > imageData.Length)
                    break;

                var entry = new ReadOnlySpan<byte>(imageData, offset, 32);
                var firstByte = entry[0];

                // Skip empty entries
                if (firstByte == 0x00)
                    continue;

                // Skip deleted entries
                if (firstByte == 0xE5 || (firstByte >= 0x05 && firstByte <= 0x0A) || (firstByte >= 0xE5 && firstByte <= 0xEA))
                    continue;

                var name = DecodeEntryName(entry);

                // Skip volume label entries
                const byte ATTR_VOLUME_LABEL = 0x08;
                if ((entry[11] & ATTR_VOLUME_LABEL) != 0)
                    continue;

                // Skip special entries
                if (name == "." || name == "..")
                    continue;

                // Check if this is the file we're looking for
                if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    // Skip directories
                    if (IsDirectoryEntry(entry))
                        return null;

                    var fileSize = BitConverter.ToUInt32(entry.Slice(28, 4));
                    var firstCluster = GetFirstCluster(entry);

                    return ReadFileFromCluster(firstCluster, fileSize, bpb);
                }
            }

            return null;
        }

        private byte[] ReadFileFromCluster(uint firstCluster, uint fileSize, Bpb bpb)
        {
            var result = new List<byte>();
            var currentCluster = firstCluster;

            while (result.Count < fileSize)
            {
                var sector = ClusterToSector(currentCluster, bpb);
                var sectorStart = (int)(sector * bpb.BytesPerSector);

                if (sectorStart + bpb.BytesPerSector > _imageData!.Length)
                    break;

                var sectorsPerCluster = bpb.SectorsPerCluster;
                for (uint s = 0; s < sectorsPerCluster; s++)
                {
                    if (result.Count >= fileSize)
                        break;

                    var byteOffset = sectorStart + (int)(s * bpb.BytesPerSector);
                    for (int b = 0; b < bpb.BytesPerSector && result.Count < fileSize; b++)
                    {
                        result.Add(_imageData![byteOffset + b]);
                    }
                }

                // Get next cluster from FAT
                currentCluster = GetNextCluster(currentCluster, bpb);
                if (currentCluster == 0 || currentCluster >= 0xFF8) // End of chain
                    break;
            }

            return result.ToArray();
        }

        /// <summary>Returns an estimate of free bytes remaining on the disk image.</summary>
        public long GetFreeBytes()
        {
            if (_imageData == null || !_isLoaded)
                return 0;

            var bpb = ParseBpb(new ReadOnlySpan<byte>(_imageData, 0, 512));
            long clusterSize = bpb.BytesPerSector * bpb.SectorsPerCluster;
            long freeBytes = 0;

            // Count free FAT entries (cluster 2 onwards)
            var fatStartSector = bpb.ReservedSectors;
            var fatOffset = (int)(fatStartSector * bpb.BytesPerSector);
            var totalClusters = IsFat12(bpb)
                ? (bpb.TotalSectors16 > 0 ? bpb.TotalSectors16 : bpb.TotalSectors32) / bpb.SectorsPerCluster
                : (bpb.TotalSectors16 > 0 ? bpb.TotalSectors16 : bpb.TotalSectors32) / bpb.SectorsPerCluster;

            for (uint cluster = 2; cluster < cluster + totalClusters && cluster < 0xFF0; cluster++)
            {
                if (GetFatEntry(cluster, bpb) == 0)
                    freeBytes += clusterSize;

                // Safety: stop before we'd overflow the FAT area
                if (IsFat12(bpb) && (int)(fatOffset + cluster * 3 / 2 + 2) > _imageData.Length)
                    break;
                if (!IsFat12(bpb) && (int)(fatOffset + cluster * 2 + 2) > _imageData.Length)
                    break;
            }

            return freeBytes;
        }

        public void CreateFolder(string targetDirectory, string folderName)
        {
            if (_imageData == null || !_isLoaded)
                throw new InvalidOperationException("Image must be opened before creating folders.");

            CreateFolderInternal(targetDirectory, folderName);
            ParseFatFilesystem();
        }

        private void CreateFolderInternal(string targetDirectory, string folderName)
        {
            var bpb = ParseBpb(new ReadOnlySpan<byte>(_imageData!, 0, 512));

            var encodedName = EncodeFileName(folderName);
            
            // Allocate 1 cluster for the directory
            var firstCluster = AllocateClusterChain(1, bpb);
            
            // Clear the cluster (fill with zeroes to avoid garbage entries)
            var clusterBytes = new byte[bpb.BytesPerSector * bpb.SectorsPerCluster];
            WriteFileToCluster(firstCluster, clusterBytes, bpb);

            uint currentDirSector;
            int currentDirSectors;
            uint parentCluster = 0; // Root is cluster 0 for '.' and '..'
            
            var rootDirStart = bpb.ReservedSectors + (bpb.NumFats * bpb.SectorsPerFat);
            var rootDirEntries = bpb.RootDirEntries > 0 ? bpb.RootDirEntries : 0;
            var rootDirSizeSectors = (int)(((rootDirEntries * 32) + (bpb.BytesPerSector - 1)) / bpb.BytesPerSector);

            if (string.IsNullOrEmpty(targetDirectory))
            {
                currentDirSector = rootDirStart;
                currentDirSectors = rootDirSizeSectors;
                parentCluster = 0;
            }
            else
            {
                var parts = targetDirectory.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                bool resolvedFromClusterChain = false;
                
                currentDirSector = rootDirStart;
                currentDirSectors = rootDirSizeSectors;

                foreach (var part in parts)
                {
                    var dirCluster = FindDirectoryCluster(currentDirSector, currentDirSectors, part, resolvedFromClusterChain, bpb);
                    if (dirCluster == 0)
                        throw new InvalidOperationException($"Directory '{part}' not found on the disk image.");

                    parentCluster = dirCluster;
                    currentDirSector = ClusterToSector(dirCluster, bpb);
                    currentDirSectors = bpb.SectorsPerCluster;
                    resolvedFromClusterChain = true;
                }
            }

            // Write 'this' and 'parent' directory entries into the new folder
            var newFolderSector = ClusterToSector(firstCluster, bpb);
            CreateDirectoryEntry(newFolderSector, bpb.SectorsPerCluster, ".          ", 0, firstCluster, true, bpb);
            CreateDirectoryEntry(newFolderSector, bpb.SectorsPerCluster, "..         ", 0, parentCluster, true, bpb);

            // Create the directory entry in the parent directory
            CreateDirectoryEntry(currentDirSector, currentDirSectors, encodedName, 0, firstCluster, true, bpb);

        }

        /// <summary>Adds a file to the root directory of the disk image.</summary>
        public void AddFile(string fileName, byte[] content)
            => AddFile(string.Empty, fileName, content);

        /// <summary>
        /// Adds a file to the specified directory inside the disk image.
        /// </summary>
        /// <param name="targetDirectory">Backslash-separated path inside the image (empty = root).</param>
        /// <param name="fileName">The host filename (8.3 encoding is applied automatically).</param>
        /// <param name="content">Raw file bytes to write.</param>
        public void AddFile(string targetDirectory, string fileName, byte[] content)
        {
            if (_imageData == null || !_isLoaded)
                throw new InvalidOperationException("Image must be opened before adding files.");

            AddFileInternal(targetDirectory, fileName, content);
            ParseFatFilesystem();
        }

        private void AddFileInternal(string targetDirectory, string fileName, byte[] content)
        {
            var bpb = ParseBpb(new ReadOnlySpan<byte>(_imageData!, 0, 512));

            // Encode filename to 8.3 format
            var encodedName = EncodeFileName(fileName);

            // Allocate cluster chain for the file content
            var firstCluster = AllocateClusterChain(content.Length, bpb);

            // Write file content to allocated clusters
            WriteFileToCluster(firstCluster, content, bpb);

            if (string.IsNullOrEmpty(targetDirectory))
            {
                // Root directory
                var rootDirStart = bpb.ReservedSectors + (bpb.NumFats * bpb.SectorsPerFat);
                var rootDirEntries = bpb.RootDirEntries > 0 ? bpb.RootDirEntries : 0;
                var rootDirSizeSectors = (int)(((rootDirEntries * 32) + (bpb.BytesPerSector - 1)) / bpb.BytesPerSector);
                CreateDirectoryEntry(rootDirStart, rootDirSizeSectors, encodedName, (uint)content.Length, firstCluster, false, bpb);
            }
            else
            {
                // Resolve subdirectory by walking the path components
                var parts = targetDirectory.Split('\\', StringSplitOptions.RemoveEmptyEntries);

                // Start search from the root directory
                var rootDirStart = bpb.ReservedSectors + (bpb.NumFats * bpb.SectorsPerFat);
                var rootDirEntries = bpb.RootDirEntries > 0 ? bpb.RootDirEntries : 0;
                var rootDirSizeSectors = (int)(((rootDirEntries * 32) + (bpb.BytesPerSector - 1)) / bpb.BytesPerSector);

                uint currentDirSector = rootDirStart;
                int currentDirSectors = rootDirSizeSectors;
                bool resolvedFromClusterChain = false;

                foreach (var part in parts)
                {
                    var dirCluster = FindDirectoryCluster(currentDirSector, currentDirSectors, part, resolvedFromClusterChain, bpb);
                    if (dirCluster == 0)
                        throw new InvalidOperationException($"Directory '{part}' not found on the disk image.");

                    currentDirSector = ClusterToSector(dirCluster, bpb);
                    currentDirSectors = bpb.SectorsPerCluster;
                    resolvedFromClusterChain = true;
                }

                // Create the directory entry in the parent directory
                CreateDirectoryEntry(currentDirSector, currentDirSectors, encodedName, (uint)content.Length, firstCluster, false, bpb);
            }
        }

        public void AddHostDirectory(string targetDirectory, string hostFolderPath)
        {
            if (_imageData == null || !_isLoaded)
                throw new InvalidOperationException("Image must be opened before adding folders.");

            AddHostDirectoryRecursive(targetDirectory, hostFolderPath);
            ParseFatFilesystem();
        }

        private void AddHostDirectoryRecursive(string targetDirectory, string hostFolderPath)
        {
            var dirInfo = new DirectoryInfo(hostFolderPath);

            CreateFolderInternal(targetDirectory, dirInfo.Name);

            var newTargetDir = string.IsNullOrEmpty(targetDirectory) ? dirInfo.Name : $"{targetDirectory}\\{dirInfo.Name}";

            foreach (var file in dirInfo.GetFiles())
            {
                var content = File.ReadAllBytes(file.FullName);
                AddFileInternal(newTargetDir, file.Name, content);
            }

            foreach (var subDir in dirInfo.GetDirectories())
            {
                AddHostDirectoryRecursive(newTargetDir, subDir.FullName);
            }
        }

        /// <summary>
        /// Searches a directory region for a subdirectory with the given name and returns its first cluster.
        /// Returns 0 if not found.
        /// </summary>
        private uint FindDirectoryCluster(uint startSector, int numSectors, string name, bool fromClusterChain, Bpb bpb)
        {
            var imageData = _imageData!;
            var entryCount = numSectors * (int)bpb.BytesPerSector / 32;

            for (int i = 0; i < entryCount; i++)
            {
                var offset = (int)(startSector * bpb.BytesPerSector) + (i * 32);

                if (offset + 32 > imageData.Length)
                    break;

                var entry = new ReadOnlySpan<byte>(imageData, offset, 32);
                var firstByte = entry[0];

                if (firstByte == 0x00) continue;
                if (firstByte == 0xE5) continue;

                var entryName = DecodeEntryName(entry);
                if (entryName == "." || entryName == "..") continue;
                if ((entry[11] & 0x08) != 0) continue; // volume label

                if (IsDirectoryEntry(entry) && entryName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return GetFirstCluster(entry);
            }

            return 0;
        }

        private string EncodeFileName(string fileName)
        {
            // Extract name and extension
            var lastDotIndex = fileName.LastIndexOf('.');
            string namePart, extPart;

            if (lastDotIndex > 0)
            {
                namePart = fileName.Substring(0, lastDotIndex);
                extPart = fileName.Substring(lastDotIndex + 1);
            }
            else
            {
                namePart = fileName;
                extPart = "";
            }

            // Truncate/Pad to 8.3 format
            if (namePart.Length > 8)
                namePart = namePart.Substring(0, 8);
            if (extPart.Length > 3)
                extPart = extPart.Substring(0, 3);

            // Convert to uppercase
            namePart = namePart.ToUpperInvariant();
            extPart = extPart.ToUpperInvariant();

            return $"{namePart.PadRight(8, ' ')}{extPart.PadRight(3, ' ')}";
        }

        private uint AllocateClusterChain(int fileSize, Bpb bpb)
        {
            var clusterSize = bpb.BytesPerSector * bpb.SectorsPerCluster;
            var clustersNeeded = (uint)((fileSize + clusterSize - 1) / clusterSize);

            var firstCluster = FindFreeCluster(bpb);
            if (firstCluster == 0)
                throw new InvalidOperationException("No free clusters available.");

            var currentCluster = firstCluster;

            for (uint i = 1; i < clustersNeeded; i++)
            {
                var nextCluster = FindFreeCluster(bpb);
                if (nextCluster == 0)
                    throw new InvalidOperationException("Not enough free clusters available.");

                SetFatEntry(currentCluster, nextCluster, bpb);
                currentCluster = nextCluster;
            }

            // Mark last cluster as end of chain
            SetFatEntry(currentCluster, 0xFFF, bpb);

            return firstCluster;
        }

        private uint FindFreeCluster(Bpb bpb)
        {
            var fatStartSector = bpb.ReservedSectors;
            var fatOffset = (int)(fatStartSector * bpb.BytesPerSector);

            // Start from cluster 2
            for (uint cluster = 2; ; cluster++)
            {
                var entryValue = GetFatEntry(cluster, bpb);

                // Check if cluster is free (entry value is 0)
                if (entryValue == 0)
                    return cluster;

                // Check for end of valid cluster range
                if (entryValue >= 0xFF8 && cluster > 10000)
                    break;
            }

            return 0;
        }

        private uint GetFatEntry(uint cluster, Bpb bpb)
        {
            var fatStartSector = bpb.ReservedSectors;
            var fatOffset = (int)(fatStartSector * bpb.BytesPerSector);

            if (IsFat12(bpb))
            {
                // FAT12
                var entryOffset = (int)(cluster * 3 / 2);
                var fatEntryStart = fatOffset + entryOffset;

                if (fatEntryStart + 2 > _imageData!.Length)
                    return 0xFFF;

                var fatValue = (ushort)(_imageData[fatEntryStart] | (_imageData[fatEntryStart + 1] << 8));

                if (cluster % 2 == 0)
                    fatValue &= 0x0FFF;
                else
                    fatValue >>= 4;

                return (uint)fatValue;
            }
            else
            {
                // FAT16
                var entryOffset = (int)(cluster * 2);
                var fatEntryStart = fatOffset + entryOffset;

                if (fatEntryStart + 2 > _imageData!.Length)
                    return 0xFFFF;

                return BitConverter.ToUInt16(_imageData.AsSpan(fatEntryStart, 2));
            }
        }

        private void SetFatEntry(uint cluster, uint value, Bpb bpb)
        {
            var fatStartSector = bpb.ReservedSectors;
            var fatOffset = (int)(fatStartSector * bpb.BytesPerSector);

            if (IsFat12(bpb))
            {
                // FAT12
                var entryOffset = (int)(cluster * 3 / 2);
                var fatEntryStart = fatOffset + entryOffset;

                if (fatEntryStart + 2 > _imageData!.Length)
                    return;

                var existingValue = (ushort)(_imageData[fatEntryStart] | (_imageData[fatEntryStart + 1] << 8));

                if (cluster % 2 == 0)
                {
                    // Clear lower 12 bits and set new value
                    existingValue &= 0xF000;
                    existingValue |= (ushort)(value & 0x0FFF);
                }
                else
                {
                    // Clear upper 4 bits and set new value
                    existingValue &= 0x0FFF;
                    existingValue |= (ushort)((value & 0x0FFF) << 4);
                }

                _imageData![fatEntryStart] = (byte)(existingValue & 0xFF);
                _imageData![fatEntryStart + 1] = (byte)((existingValue >> 8) & 0xFF);
            }
            else
            {
                // FAT16
                var entryOffset = (int)(cluster * 2);
                var fatEntryStart = fatOffset + entryOffset;

                if (fatEntryStart + 2 > _imageData!.Length)
                    return;

                var value16 = (ushort)value;
                _imageData![fatEntryStart] = (byte)(value16 & 0xFF);
                _imageData![fatEntryStart + 1] = (byte)((value16 >> 8) & 0xFF);
            }
        }

        private void WriteFileToCluster(uint firstCluster, byte[] content, Bpb bpb)
        {
            var currentCluster = firstCluster;
            var offset = 0;

            while (offset < content.Length)
            {
                var sector = ClusterToSector(currentCluster, bpb);
                var sectorStart = (int)(sector * bpb.BytesPerSector);

                var sectorsPerCluster = bpb.SectorsPerCluster;
                for (uint s = 0; s < sectorsPerCluster; s++)
                {
                    if (offset >= content.Length)
                        break;

                    var byteOffset = sectorStart + (int)(s * bpb.BytesPerSector);
                    for (int b = 0; b < bpb.BytesPerSector && offset < content.Length; b++)
                    {
                        _imageData![byteOffset + b] = content[offset++];
                    }
                }

                // Get next cluster from FAT
                currentCluster = GetFatEntry(currentCluster, bpb);
                if (currentCluster >= 0xFF8)
                    break;
            }
        }

        private void CreateDirectoryEntry(uint startSector, int numSectors, string fileName, uint fileSize, uint firstCluster, bool isDirectory, Bpb bpb)
        {
            var imageData = _imageData!;
            var entryCount = numSectors * 512 / 32;

            for (int i = 0; i < entryCount; i++)
            {
                var offset = (int)(startSector * 512) + (i * 32);

                if (offset + 32 > imageData.Length)
                    break;

                var entry = imageData.AsSpan(offset, 32);
                var firstByte = entry[0];

                // Check for empty or deleted entry
                if (firstByte == 0x00 || firstByte == 0xE5)
                {
                    // Create new entry
                    Array.Clear(imageData, offset, 32);

                    // Name (8 bytes) + Extension (3 bytes)
                    for (int j = 0; j < 11 && j < fileName.Length; j++)
                    {
                        imageData[offset + j] = (byte)fileName[j];
                    }

                    // Attributes (byte 11)
                    if (isDirectory)
                        imageData[offset + 11] = 0x10; // Directory
                    else
                        imageData[offset + 11] = 0x00; // Normal file

                    // Timestamps (bytes 22-25)
                    var now = DateTime.Now;
                    ushort fatTime = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
                    ushort fatDate = (ushort)(((Math.Max(1980, now.Year) - 1980) << 9) | (now.Month << 5) | now.Day);
                    
                    var timeBytes = BitConverter.GetBytes(fatTime);
                    imageData[offset + 22] = timeBytes[0];
                    imageData[offset + 23] = timeBytes[1];
                    
                    var dateBytes = BitConverter.GetBytes(fatDate);
                    imageData[offset + 24] = dateBytes[0];
                    imageData[offset + 25] = dateBytes[1];

                    // First cluster (bytes 26-27)
                    var clusterBytes = BitConverter.GetBytes((ushort)firstCluster);
                    imageData[offset + 26] = clusterBytes[0];
                    imageData[offset + 27] = clusterBytes[1];

                    // File size (bytes 28-31)
                    var sizeBytes = BitConverter.GetBytes(fileSize);
                    imageData[offset + 28] = sizeBytes[0];
                    imageData[offset + 29] = sizeBytes[1];
                    imageData[offset + 30] = sizeBytes[2];
                    imageData[offset + 31] = sizeBytes[3];

                    return;
                }
            }

            throw new InvalidOperationException("No free directory entries available.");
        }

        public void DeleteEntry(string path)
        {
            if (_imageData == null || !_isLoaded)
                throw new InvalidOperationException("Image must be opened before deleting files.");

            var bpb = ParseBpb(new ReadOnlySpan<byte>(_imageData, 0, 512));

            var lastSlash = path.LastIndexOf('\\');
            var targetDirectory = lastSlash >= 0 ? path.Substring(0, lastSlash) : string.Empty;
            var name = lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;

            var rootDirStart = bpb.ReservedSectors + (bpb.NumFats * bpb.SectorsPerFat);
            var rootDirEntries = bpb.RootDirEntries > 0 ? bpb.RootDirEntries : 0;
            var rootDirSizeSectors = (int)(((rootDirEntries * 32) + (bpb.BytesPerSector - 1)) / bpb.BytesPerSector);

            uint currentDirSector = rootDirStart;
            int currentDirSectors = rootDirSizeSectors;

            if (!string.IsNullOrEmpty(targetDirectory))
            {
                var parts = targetDirectory.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                bool resolvedFromClusterChain = false;

                foreach (var part in parts)
                {
                    var dirCluster = FindDirectoryCluster(currentDirSector, currentDirSectors, part, resolvedFromClusterChain, bpb);
                    if (dirCluster == 0)
                        throw new FileNotFoundException($"Directory '{part}' not found on the disk image.");

                    currentDirSector = ClusterToSector(dirCluster, bpb);
                    currentDirSectors = bpb.SectorsPerCluster;
                    resolvedFromClusterChain = true;
                }
            }

            DeleteEntryInternal(currentDirSector, currentDirSectors, name, bpb);

            ParseFatFilesystem();
        }

        private void DeleteEntryInternal(uint startSector, int numSectors, string name, Bpb bpb)
        {
            var imageData = _imageData!;
            var entryCount = numSectors * 512 / 32;

            for (int i = 0; i < entryCount; i++)
            {
                var offset = (int)(startSector * 512) + (i * 32);

                if (offset + 32 > imageData.Length)
                    break;

                var entry = new ReadOnlySpan<byte>(imageData, offset, 32);
                var firstByte = entry[0];

                // Skip empty entries
                if (firstByte == 0x00)
                    continue;

                // Skip already deleted entries
                if (firstByte == 0xE5)
                    continue;

                var entryName = DecodeEntryName(entry);

                // Skip volume label entries
                if ((entry[11] & 0x08) != 0)
                    continue;

                // Skip special entries
                if (entryName == "." || entryName == "..")
                    continue;

                // Check if this is the entry we're looking for
                if (entryName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    // Mark as deleted by setting first byte to 0xE5
                    imageData[offset] = 0xE5;

                    // If it's a file, free its cluster chain
                    if (!IsDirectoryEntry(entry))
                    {
                        var firstCluster = GetFirstCluster(entry);
                        FreeClusterChain(firstCluster, bpb);
                    }
                    // If it's a directory, recursively delete its contents
                    else if (IsDirectoryEntry(entry))
                    {
                        var firstCluster = GetFirstCluster(entry);
                        var dirStartSector = ClusterToSector(firstCluster, bpb);
                        var dirSizeBytes = BitConverter.ToUInt32(entry.Slice(28, 4));
                        var dirSizeSectors = (int)((dirSizeBytes + bpb.BytesPerSector - 1) / bpb.BytesPerSector);

                        // Delete all entries in the subdirectory
                        for (int j = 0; j < dirSizeSectors * 512 / 32; j++)
                        {
                            var subOffset = (int)(dirStartSector * 512) + (j * 32);
                            if (subOffset + 32 <= imageData.Length)
                            {
                                var subFirstByte = imageData[subOffset];
                                if (subFirstByte != 0x00 && subFirstByte != 0xE5)
                                {
                                    var subEntryName = DecodeEntryName(new ReadOnlySpan<byte>(imageData, subOffset, 32));
                                    if (subEntryName != "." && subEntryName != ".." && (imageData[subOffset + 11] & 0x08) == 0)
                                    {
                                        imageData[subOffset] = 0xE5;
                                        if (!IsDirectoryEntry(new ReadOnlySpan<byte>(imageData, subOffset, 32)))
                                        {
                                            var subCluster = BitConverter.ToUInt16(imageData.AsSpan(subOffset + 26, 2));
                                            FreeClusterChain(subCluster, bpb);
                                        }
                                    }
                                }
                            }
                        }

                        // Free the directory's cluster chain
                        FreeClusterChain(firstCluster, bpb);
                    }

                    return;
                }
            }

            throw new FileNotFoundException($"Entry '{name}' not found.");
        }

        private void FreeClusterChain(uint firstCluster, Bpb bpb)
        {
            if (firstCluster == 0 || firstCluster >= 0xFF8)
                return;

            var currentCluster = firstCluster;

            while (currentCluster < 0xFF8)
            {
                var nextCluster = GetFatEntry(currentCluster, bpb);
                SetFatEntry(currentCluster, 0, bpb); // Mark as free
                currentCluster = nextCluster;
            }
        }

        private uint GetNextCluster(uint currentCluster, Bpb bpb)
        {
            // Calculate FAT entry offset (FAT12 uses 1.5 bytes per entry, FAT16 uses 2 bytes)
            var fatStartSector = bpb.ReservedSectors;
            var fatOffset = (int)(fatStartSector * bpb.BytesPerSector);

            // For FAT12: each entry is 1.5 bytes
            if (IsFat12(bpb))
            {
                var entryOffset = (int)(currentCluster * 3 / 2);
                var fatEntryStart = fatOffset + entryOffset;

                if (fatEntryStart + 2 > _imageData!.Length)
                    return 0;

                var fatValue = (ushort)(_imageData[fatEntryStart] | (_imageData[fatEntryStart + 1] << 8));

                if (currentCluster % 2 == 0)
                {
                    fatValue &= 0x0FFF;
                }
                else
                {
                    fatValue >>= 4;
                }

                return (uint)fatValue;
            }
            else
            {
                // FAT16: each entry is 2 bytes
                var entryOffset = (int)(currentCluster * 2);
                var fatEntryStart = fatOffset + entryOffset;

                if (fatEntryStart + 2 > _imageData!.Length)
                    return 0;

                return BitConverter.ToUInt16(_imageData.AsSpan(fatEntryStart, 2));
            }
        }

        private struct Bpb
        {
            public uint BytesPerSector { get; set; }
            public byte SectorsPerCluster { get; set; }
            public uint ReservedSectors { get; set; }
            public byte NumFats { get; set; }
            public uint RootDirEntries { get; set; }
            public uint TotalSectors16 { get; set; }
            public byte MediaDescriptor { get; set; }
            public uint SectorsPerFat16 { get; set; }
            public uint SectorsPerTrack { get; set; }
            public uint HeadsPerCylinder { get; set; }
            public uint HiddenSectors { get; set; }
            public uint TotalSectors32 { get; set; }
            public uint SectorsPerFat { get; set; }
        }
    }
}
