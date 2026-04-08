using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NetImage.Models;

namespace NetImage.Workers
{
    public record FileEntry(string Path, long? Size, DateTime? Modified);
    public readonly record struct OperationProgress(long ProcessedBytes, long TotalBytes, string CurrentItem);

    public class DiskImageWorker
    {
        private byte[]? _imageData;
        private bool _isLoaded;
        private uint _partitionStartSector;
        private FatType? _fatType;

        public event EventHandler? LoadingStarted;
        public event EventHandler? LoadingCompleted;

        private enum ParseResult
        {
            Loaded,
            UnsupportedFat32
        }

        public DiskImageWorker(string filePath)
        {
            FilePath = filePath;
            FilesAndFolders = new List<FileEntry>();
        }

        public string FilePath { get; set; }

        public string VolumeLabel { get; private set; } = string.Empty;

        public List<FileEntry> FilesAndFolders { get; private set; }
        public IReadOnlyList<MbrPartition> Partitions => _partitions;
        private readonly List<MbrPartition> _partitions = new();

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
            var parseResult = ParseFatFilesystem();
            _isLoaded = parseResult == ParseResult.Loaded;

            LoadingCompleted?.Invoke(this, EventArgs.Empty);
        }

        public async Task SaveAsync(string imageName)
        {
            if (_imageData == null)
                throw new InvalidOperationException("No image data to save.");

            await File.WriteAllBytesAsync(imageName, _imageData);
        }

        private ParseResult ParseFatFilesystem()
        {
            FilesAndFolders.Clear();
            VolumeLabel = string.Empty;
            _fatType = null;
            _partitionStartSector = 0;
            _partitions.Clear();

            if (_imageData == null || _imageData.Length < 512)
            {
                return ParseResult.Loaded;
            }

            var bootSector = new ReadOnlySpan<byte>(_imageData, 0, 512);

            // Check if this is an MBR partition table (hard disk image)
            _partitionStartSector = CheckForPartitionTable(bootSector);

            System.Diagnostics.Debug.WriteLine($"Partition start sector: {_partitionStartSector}, byte offset: {_partitionStartSector * 512}");

            // If partition found, read boot sector from partition start
            if (_partitionStartSector > 0)
            {
                var partitionByteOffset = _partitionStartSector * 512;
                if (partitionByteOffset + 512 > _imageData.Length)
                {
                    System.Diagnostics.Debug.WriteLine($"Partition offset out of bounds: {partitionByteOffset} + 512 > {_imageData.Length}");
                    return ParseResult.Loaded;
                }
                bootSector = new ReadOnlySpan<byte>(_imageData, (int)partitionByteOffset, 512);
                System.Diagnostics.Debug.WriteLine($"Read boot sector from partition at sector {_partitionStartSector} (byte offset {partitionByteOffset})");
            }

            if (!IsFatBootSector(bootSector))
            {
                System.Diagnostics.Debug.WriteLine($"FAT boot sector check failed at sector {_partitionStartSector}");
                System.Diagnostics.Debug.WriteLine($"First 16 bytes: [{string.Join(" ", bootSector.Slice(0, 16).ToArray().Select(b => b.ToString("X2")))}]");
                System.Diagnostics.Debug.WriteLine($"Bytes 11-15 (BPB area): [{string.Join(" ", bootSector.Slice(11, 5).ToArray().Select(b => b.ToString("X2")))}]");
                System.Diagnostics.Debug.WriteLine($"Signature bytes 510-511: {bootSector[510]:X2} {bootSector[511]:X2}");
                return ParseResult.Loaded;
            }

            System.Diagnostics.Debug.WriteLine($"FAT boot sector found at sector {_partitionStartSector}");

            var bpb = ParseBpb(bootSector);
            _fatType = GetFatType(bpb);
            if (_fatType == FatType.Fat32)
            {
                System.Diagnostics.Debug.WriteLine("FAT32 is not supported.");
                return ParseResult.UnsupportedFat32;
            }

            System.Diagnostics.Debug.WriteLine($"BPB: BytesPerSector={bpb.BytesPerSector}, SectorsPerCluster={bpb.SectorsPerCluster}, ReservedSectors={bpb.ReservedSectors}, NumFats={bpb.NumFats}, RootDirEntries={bpb.RootDirEntries}, TotalSectors16={bpb.TotalSectors16}, TotalSectors32={bpb.TotalSectors32}, SectorsPerFat16={bpb.SectorsPerFat16}, SectorsPerFat={bpb.SectorsPerFat}");

            var rootDirectory = GetRootDirectory(bpb);

            System.Diagnostics.Debug.WriteLine($"Root directory: startSector={rootDirectory.StartSector}, sizeSectors={rootDirectory.NumSectors}, entries={bpb.RootDirEntries}");

            ExtractVolumeLabel(rootDirectory.StartSector, rootDirectory.NumSectors, bpb);

            if (string.IsNullOrWhiteSpace(VolumeLabel))
            {
                VolumeLabel = System.IO.Path.GetFileNameWithoutExtension(FilePath);
            }

            ReadDirectoryEntries(rootDirectory, string.Empty, bpb);
            return ParseResult.Loaded;
        }

        /// <summary>
        /// Checks if the boot sector is an MBR partition table and returns the offset of the first FAT partition.
        /// Returns 0 if no partition table is found (image is a raw filesystem).
        /// </summary>
        private uint CheckForPartitionTable(ReadOnlySpan<byte> sector)
        {
            if (sector.Length < 512)
                return 0;

            // MBR signature at end
            if (sector[510] != 0x55 || sector[511] != 0xAA)
            {
                System.Diagnostics.Debug.WriteLine($"MBR signature check failed: {sector[510]:X2} {sector[511]:X2}");
                return 0;
            }

            System.Diagnostics.Debug.WriteLine("MBR signature found, checking partition table...");

            // Check if this looks like a FAT BPB (boot sector) rather than MBR
            // FAT boot sectors have a jump instruction at offset 0 (0xEB) followed by BPB fields
            // MBR has boot code at the start, and partition table at offset 446
            //
            // Key difference: FAT BPB has bytes/sector at offset 11 that is a power of 2 (512, 1024, etc.)
            // and sectors/cluster at offset 13 (1, 2, 4, 8, 16, 32, 64)
            // MBR doesn't have these meaningful values
            var bytesPerSector = BitConverter.ToUInt16(sector.Slice(11, 2));
            var sectorsPerCluster = sector[13];
            var reservedSectors = BitConverter.ToUInt16(sector.Slice(14, 2));

            System.Diagnostics.Debug.WriteLine($"BPB check: jump={sector[0]:X2}, bytesPerSector={bytesPerSector}, sectorsPerCluster={sectorsPerCluster}, reservedSectors={reservedSectors}");

            // Check if this looks like a valid FAT BPB
            // Must start with 0xEB (jump instruction) AND have valid BPB values AND have OEM ID
            bool hasFatJump = sector[0] == 0xEB;
            bool hasValidBytesPerSector = (bytesPerSector == 512 || bytesPerSector == 1024 || bytesPerSector == 2048 || bytesPerSector == 4096);
            bool hasValidSectorsPerCluster = (sectorsPerCluster == 1 || sectorsPerCluster == 2 || sectorsPerCluster == 4 ||
                                              sectorsPerCluster == 8 || sectorsPerCluster == 16 || sectorsPerCluster == 32 || sectorsPerCluster == 64);

            // FAT boot sectors have OEM ID at offset 3-10 (e.g., "MSDOS5.0", "FAT16   ", "MSWIN4.1")
            // MBR boot code doesn't have printable ASCII there - it's random machine code
            bool hasFatOemId = true;
            for (int i = 3; i < Math.Min(11, sector.Length); i++)
            {
                byte b = sector[i];
                // OEM ID should be printable ASCII or space (0x20-0x7E)
                if (b != 0x20 && (b < 0x20 || b > 0x7E))
                {
                    hasFatOemId = false;
                    break;
                }
            }

            bool looksLikeFatBpb = hasFatJump && hasValidBytesPerSector && hasValidSectorsPerCluster && hasFatOemId;

            if (looksLikeFatBpb)
            {
                System.Diagnostics.Debug.WriteLine("Looks like raw FAT BPB, not MBR");
                // This looks like a raw FAT filesystem, not an MBR
                return 0;
            }

            System.Diagnostics.Debug.WriteLine("Not a FAT BPB, checking partition table...");

            uint firstFatPartitionStart = 0;

            // Check partition table at offset 446
            // Look for any partition with a FAT type
            for (int i = 0; i < 4; i++)
            {
                var offset = 446 + (i * 16);
                var bootIndicator = sector[offset];
                var partitionType = sector[offset + 4];
                var startSector = BitConverter.ToUInt32(sector.Slice(offset + 8, 4));
                var sectorCount = BitConverter.ToUInt32(sector.Slice(offset + 12, 4));

                // Debug: print raw bytes of partition entry
                var entryBytes = string.Join(" ", sector.Slice(offset, 16).ToArray().Select(b => b.ToString("X2")));
                System.Diagnostics.Debug.WriteLine($"Partition {i}: boot={bootIndicator:X2}, type={partitionType:X2}, start={startSector}, count={sectorCount}, raw=[{entryBytes}]");

                if (partitionType != 0x00)
                {
                    _partitions.Add(new MbrPartition(i, bootIndicator == 0x80, partitionType, startSector, sectorCount));
                }

                // FAT partition types: 0x01, 0x04, 0x06 (FAT12/16), 0x0B, 0x0C (FAT32), 0x0E, 0x0F (large FAT12/16), 0x14, 0x16
                if (firstFatPartitionStart == 0 && (partitionType == 0x01 || partitionType == 0x04 || partitionType == 0x06 ||
                    partitionType == 0x0B || partitionType == 0x0C || partitionType == 0x0E ||
                    partitionType == 0x0F || partitionType == 0x14 || partitionType == 0x16))
                {
                    System.Diagnostics.Debug.WriteLine($"Found FAT partition type {partitionType:X2} at sector {startSector}");
                    // Found a FAT partition, remember its start sector
                    firstFatPartitionStart = startSector;
                }
            }

            if (firstFatPartitionStart == 0)
                System.Diagnostics.Debug.WriteLine("No FAT partition found");
                
            return firstFatPartitionStart;
        }

        private void ExtractVolumeLabel(uint startSector, int numSectors, Bpb bpb)
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

                if (firstByte == 0x00 || firstByte == 0xE5)
                    continue;

                // Skip LFN entries - they are not volume labels despite having bit 3 set
                if (IsLfNEntry(entry))
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
            if (sector.Length < 512)
                return false;

            // Check MBR signature
            if (sector[510] != 0x55 || sector[511] != 0xAA)
                return false;

            // Validate BPB fields to ensure this is actually a FAT boot sector
            // and not just random data with 0x55AA at the end
            var bytesPerSector = BitConverter.ToUInt16(sector.Slice(11, 2));
            var sectorsPerCluster = sector[13];
            var reservedSectors = BitConverter.ToUInt16(sector.Slice(14, 2));

            // bytesPerSector must be a valid power of 2
            if (bytesPerSector != 512 && bytesPerSector != 1024 && bytesPerSector != 2048 && bytesPerSector != 4096)
                return false;

            // sectorsPerCluster must be a valid power of 2
            if (sectorsPerCluster != 1 && sectorsPerCluster != 2 && sectorsPerCluster != 4 &&
                sectorsPerCluster != 8 && sectorsPerCluster != 16 && sectorsPerCluster != 32 && sectorsPerCluster != 64)
                return false;

            // reservedSectors should be reasonable (typically 1-32 for FAT12/16, up to 128 for FAT32)
            if (reservedSectors == 0 || reservedSectors > 256)
                return false;

            return true;
        }

        private FatType GetFatType(Bpb bpb)
        {
            if (bpb.ClusterCount < 4085)
                return FatType.Fat12;

            if (bpb.ClusterCount < 65525)
                return FatType.Fat16;

            return FatType.Fat32;
        }

        private bool IsFat12(Bpb bpb) => GetFatType(bpb) == FatType.Fat12;

        private bool IsEndOfChain(uint clusterValue, Bpb bpb)
        {
            return GetFatType(bpb) switch
            {
                FatType.Fat12 => clusterValue >= 0x0FF8,
                FatType.Fat16 => clusterValue >= 0xFFF8,
                _ => clusterValue >= 0x0FFFFFF8
            };
        }

        private uint GetEndOfChainMarker(Bpb bpb)
        {
            return GetFatType(bpb) switch
            {
                FatType.Fat12 => 0x0FFF,
                FatType.Fat16 => 0xFFFF,
                _ => 0x0FFFFFFF
            };
        }

        private bool IsDataCluster(uint cluster, Bpb bpb)
            => cluster >= 2 && cluster < bpb.ClusterCount + 2;

        private uint GetTotalSectors(Bpb bpb)
            => bpb.TotalSectors16 != 0 ? bpb.TotalSectors16 : bpb.TotalSectors32;

        private uint GetRootDirSizeSectors(Bpb bpb)
            => ((bpb.RootDirEntries * 32) + (bpb.BytesPerSector - 1)) / bpb.BytesPerSector;

        private uint GetRootDirStartSector(Bpb bpb)
            => _partitionStartSector + bpb.ReservedSectors + (bpb.NumFats * bpb.SectorsPerFat);

        private uint GetDataStartSector(Bpb bpb)
            => GetRootDirStartSector(bpb) + GetRootDirSizeSectors(bpb);

        private DirectoryLocation GetRootDirectory(Bpb bpb)
            => new(GetRootDirStartSector(bpb), (int)GetRootDirSizeSectors(bpb), 0);

        private DirectoryLocation GetSubdirectoryLocation(uint firstCluster, Bpb bpb)
            => new(ClusterToSector(firstCluster, bpb), bpb.SectorsPerCluster, firstCluster);

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

            bpb.SectorsPerFat = bpb.SectorsPerFat16 != 0
                ? bpb.SectorsPerFat16
                : BitConverter.ToUInt32(bootSector.Slice(36, 4));

            var totalSectors = GetTotalSectors(bpb);
            var rootDirSizeSectors = GetRootDirSizeSectors(bpb);
            var dataAreaStart = bpb.ReservedSectors + (bpb.NumFats * bpb.SectorsPerFat) + rootDirSizeSectors;
            var dataSectors = totalSectors > dataAreaStart ? totalSectors - dataAreaStart : 0;
            bpb.ClusterCount = bpb.SectorsPerCluster == 0 ? 0 : dataSectors / bpb.SectorsPerCluster;

            return bpb;
        }

        private uint GetRequiredClusterCount(int fileSize, Bpb bpb)
        {
            if (fileSize <= 0)
                return 0;

            var clusterSize = (long)bpb.BytesPerSector * bpb.SectorsPerCluster;
            return (uint)(((long)fileSize + clusterSize - 1) / clusterSize);
        }

        private List<uint> FindFreeClusters(uint clustersNeeded, Bpb bpb)
        {
            var freeClusters = new List<uint>((int)clustersNeeded);
            if (clustersNeeded == 0)
                return freeClusters;

            for (uint cluster = 2; cluster < bpb.ClusterCount + 2; cluster++)
            {
                if (GetFatEntry(cluster, bpb) != 0)
                    continue;

                freeClusters.Add(cluster);
                if (freeClusters.Count == clustersNeeded)
                    break;
            }

            return freeClusters;
        }

        private void LinkClusterChain(IReadOnlyList<uint> clusters, Bpb bpb)
        {
            if (clusters.Count == 0)
                return;

            for (int i = 0; i < clusters.Count - 1; i++)
            {
                SetFatEntry(clusters[i], clusters[i + 1], bpb);
            }

            SetFatEntry(clusters[^1], GetEndOfChainMarker(bpb), bpb);
        }

        private void FreeClusters(IEnumerable<uint> clusters, Bpb bpb)
        {
            foreach (var cluster in clusters)
            {
                SetFatEntry(cluster, 0, bpb);
            }
        }

        private void UpdateDirectoryEntry(int offset, uint fileSize, uint firstCluster)
        {
            var imageData = _imageData!;
            var sizeBytes = BitConverter.GetBytes(fileSize);
            for (int i = 0; i < 4; i++)
                imageData[offset + 28 + i] = sizeBytes[i];

            var clusterBytes = BitConverter.GetBytes((ushort)firstCluster);
            imageData[offset + 26] = clusterBytes[0];
            imageData[offset + 27] = clusterBytes[1];

            var now = DateTime.Now;
            ushort fatTime = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
            ushort fatDate = (ushort)(((Math.Max(1980, now.Year) - 1980) << 9) | (now.Month << 5) | now.Day);

            var timeBytes = BitConverter.GetBytes(fatTime);
            imageData[offset + 22] = timeBytes[0];
            imageData[offset + 23] = timeBytes[1];

            var dateBytes = BitConverter.GetBytes(fatDate);
            imageData[offset + 24] = dateBytes[0];
            imageData[offset + 25] = dateBytes[1];
        }

        private void ClearCluster(uint cluster, Bpb bpb)
        {
            var startSector = ClusterToSector(cluster, bpb);
            var clusterOffset = (int)(startSector * bpb.BytesPerSector);
            var clusterSize = (int)(bpb.BytesPerSector * bpb.SectorsPerCluster);
            if (clusterOffset + clusterSize > _imageData!.Length)
                throw new InvalidOperationException("Cluster points outside the image bounds.");

            Array.Clear(_imageData, clusterOffset, clusterSize);
        }

        private uint ExpandDirectory(DirectoryLocation directory, Bpb bpb)
        {
            if (!IsDataCluster(directory.FirstCluster, bpb))
                throw new InvalidOperationException("No free directory entries available.");

            uint lastCluster = 0;
            foreach (var cluster in EnumerateClusterChain(directory.FirstCluster, bpb))
            {
                lastCluster = cluster;
            }

            if (lastCluster == 0)
                throw new InvalidOperationException("No free directory entries available.");

            var freeClusters = FindFreeClusters(1, bpb);
            if (freeClusters.Count == 0)
                throw new InvalidOperationException("No free directory entries available.");

            var newCluster = freeClusters[0];
            ClearCluster(newCluster, bpb);
            SetFatEntry(newCluster, GetEndOfChainMarker(bpb), bpb);

            try
            {
                SetFatEntry(lastCluster, newCluster, bpb);
            }
            catch
            {
                SetFatEntry(newCluster, 0, bpb);
                throw;
            }

            return newCluster;
        }

        private IEnumerable<uint> EnumerateClusterChain(uint firstCluster, Bpb bpb)
        {
            if (!IsDataCluster(firstCluster, bpb))
                yield break;

            var currentCluster = firstCluster;
            var visited = new HashSet<uint>();

            while (IsDataCluster(currentCluster, bpb))
            {
                if (!visited.Add(currentCluster))
                {
                    System.Diagnostics.Debug.WriteLine($"Cycle detected at cluster {currentCluster}");
                    yield break;
                }

                yield return currentCluster;

                var nextCluster = GetNextCluster(currentCluster, bpb);
                if (nextCluster == 0 || IsEndOfChain(nextCluster, bpb))
                    yield break;

                currentCluster = nextCluster;
            }
        }

        private IEnumerable<int> EnumerateDirectoryEntryOffsets(DirectoryLocation directory, Bpb bpb)
        {
            if (directory.FirstCluster == 0)
            {
                foreach (var offset in EnumerateDirectoryEntryOffsets(directory.StartSector, directory.NumSectors, bpb))
                    yield return offset;

                yield break;
            }

            foreach (var cluster in EnumerateClusterChain(directory.FirstCluster, bpb))
            {
                var startSector = ClusterToSector(cluster, bpb);
                foreach (var offset in EnumerateDirectoryEntryOffsets(startSector, bpb.SectorsPerCluster, bpb))
                    yield return offset;
            }
        }

        private IEnumerable<int> EnumerateDirectoryEntryOffsets(uint startSector, int numSectors, Bpb bpb)
        {
            var imageData = _imageData!;
            var entryCount = numSectors * (int)bpb.BytesPerSector / 32;

            for (int i = 0; i < entryCount; i++)
            {
                var offset = (int)(startSector * bpb.BytesPerSector) + (i * 32);

                if (offset + 32 > imageData.Length)
                    yield break;

                yield return offset;
            }
        }

        private bool TryResolveDirectory(string path, Bpb bpb, out DirectoryLocation directory)
        {
            directory = GetRootDirectory(bpb);

            if (string.IsNullOrEmpty(path))
                return true;

            foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var dirCluster = FindDirectoryCluster(directory, part, bpb);
                if (dirCluster == 0)
                    return false;

                directory = GetSubdirectoryLocation(dirCluster, bpb);
            }

            return true;
        }

        private void ReadDirectoryEntries(DirectoryLocation directory, string parentPath, Bpb bpb)
        {
            var imageData = _imageData!;

            foreach (var offset in EnumerateDirectoryEntryOffsets(directory, bpb))
            {
                var entry = new ReadOnlySpan<byte>(imageData, offset, 32);
                var firstByte = entry[0];

                if (firstByte == 0x00)
                    break;

                if (IsDeletedEntry(firstByte))
                    continue;

                // Skip LFN entries - they're processed when we hit the short name entry
                if (IsLfNEntry(entry))
                {
                    continue;
                }

                const byte ATTR_VOLUME_LABEL = 0x08;
                if ((entry[11] & ATTR_VOLUME_LABEL) != 0)
                    continue;

                // Try to read LFN first, fall back to short name if no LFN
                var shortName = DecodeEntryName(entry);
                var longName = ReadLongFileName(offset, bpb);
                var name = longName ?? shortName;

                if (name == "." || name == "..")
                    continue;

                var fullPath = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}\\{name}";
                var isDir = IsDirectoryEntry(entry);
                var fileSize = isDir ? (long?)null : (long)BitConverter.ToUInt32(entry.Slice(28, 4));
                var modified = ParseFatDateTime(BitConverter.ToUInt16(entry.Slice(24, 2)), BitConverter.ToUInt16(entry.Slice(22, 2)));
                FilesAndFolders.Add(new FileEntry(fullPath, fileSize, modified));

                if (isDir)
                {
                    var firstCluster = GetFirstCluster(entry);
                    if (IsDataCluster(firstCluster, bpb))
                    {
                        ReadDirectoryEntries(GetSubdirectoryLocation(firstCluster, bpb), fullPath, bpb);
                    }
                }
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

        private static bool IsDeletedEntry(byte firstByte)
            => firstByte == 0xE5;

        // LFN (Long File Name) constants per VFAT specification
        private const byte ATTR_LFN = 0x0F; // Combination of READ_ONLY + HIDDEN + SYSTEM + VOLUME_LABEL
        private const int LFN_NAME_PART1_SIZE = 5; // Bytes 0-9: 5 Unicode chars (10 bytes)
        private const int LFN_NAME_PART2_SIZE = 6; // Bytes 14-25: 6 Unicode chars (12 bytes)
        private const int LFN_NAME_PART3_SIZE = 2; // Bytes 28-31: 2 Unicode chars (4 bytes)
        private const int LFN_CHARS_PER_ENTRY = 13; // Max Unicode chars per LFN entry

        /// <summary>
        /// Checks if a directory entry is an LFN (Long File Name) entry.
        /// LFN entries have attribute byte 0x0F (all special attribute bits set).
        /// </summary>
        private static bool IsLfNEntry(ReadOnlySpan<byte> entry)
        {
            return (entry[11] & ATTR_LFN) == ATTR_LFN;
        }

        /// <summary>
        /// Gets the LFN sequence number from an LFN entry.
        /// Lower 5 bits = sequence number, bit 6 = last-entry flag.
        /// </summary>
        private static byte GetLfNSequenceNumber(byte firstByte)
        {
            return (byte)(firstByte & 0x1F);
        }

        /// <summary>
        /// Checks if this is the last LFN entry (closest to short name entry).
        /// Bit 6 is set on the last entry.
        /// </summary>
        private static bool IsLastLfNEntry(byte firstByte)
        {
            return (firstByte & 0x40) != 0;
        }

        /// <summary>
        /// Calculates the VFAT checksum for an 8.3 name.
        /// This is used to link LFN entries with their corresponding 8.3 entry.
        /// </summary>
        private static byte CalculateChecksum(ReadOnlySpan<byte> shortName)
        {
            byte checksum = 0;
            for (int i = 0; i < 11; i++)
            {
                checksum = (byte)(((checksum & 1) == 1 ? (byte)0x80 : (byte)0) ^ (byte)(checksum >> 1) ^ shortName[i]);
            }
            return checksum;
        }

        /// <summary>
        /// Decodes Unicode characters from an LFN entry.
        /// LFN entries store 13 Unicode characters in UTF-16LE format at specific offsets.
        /// </summary>
        private static string DecodeLfNEntry(ReadOnlySpan<byte> entry)
        {
            var result = new System.Text.StringBuilder(LFN_CHARS_PER_ENTRY);

            // Part 1: 5 characters at offsets 0-9 (2 bytes each)
            for (int i = 0; i < LFN_NAME_PART1_SIZE; i++)
            {
                int offset = i * 2;
                ushort ch = BitConverter.ToUInt16(entry.Slice(offset, 2));
                if (ch == 0xFFFF || ch == 0x0000) return result.ToString();
                result.Append((char)ch);
            }

            // Part 2: 6 characters at offsets 14-25 (2 bytes each)
            for (int i = 0; i < LFN_NAME_PART2_SIZE; i++)
            {
                int offset = 14 + i * 2;
                ushort ch = BitConverter.ToUInt16(entry.Slice(offset, 2));
                if (ch == 0xFFFF || ch == 0x0000) return result.ToString();
                result.Append((char)ch);
            }

            // Part 3: 2 characters at offsets 28-31 (2 bytes each)
            for (int i = 0; i < LFN_NAME_PART3_SIZE; i++)
            {
                int offset = 28 + i * 2;
                ushort ch = BitConverter.ToUInt16(entry.Slice(offset, 2));
                if (ch == 0xFFFF || ch == 0x0000) return result.ToString();
                result.Append((char)ch);
            }

            return result.ToString();
        }

        /// <summary>
        /// Reads consecutive LFN entries preceding a short name entry and combines them into a full name.
        /// LFN entries are stored in reverse order (last entry closest to short name).
        /// </summary>
        private string? ReadLongFileName(int shortNameOffset, Bpb bpb)
        {
            var imageData = _imageData!;
            var shortNameEntry = new ReadOnlySpan<byte>(imageData, shortNameOffset, 32);

            // Get expected checksum from the short name entry
            byte expectedChecksum = CalculateChecksum(shortNameEntry);

            // Check if the previous entry is an LFN entry
            int offset = shortNameOffset - 32;

            // Make sure we don't go out of bounds
            if (offset < 0 || offset + 32 > imageData.Length)
            {
                return null;
            }

            var entry = new ReadOnlySpan<byte>(imageData, offset, 32);
            byte firstByte = entry[0];

            // Stop if not an LFN entry
            if (firstByte == 0x00 || firstByte == 0xE5 || !IsLfNEntry(entry))
            {
                return null; // No LFN
            }

            var sequenceNumber = GetLfNSequenceNumber(firstByte);
            if (sequenceNumber == 0)
            {
                return null; // Invalid sequence number
            }

            var lfnParts = new List<string>(sequenceNumber);
            int lfnEntriesRead = 0;

            // Read LFN entries backwards until we reach the first one
            while (lfnEntriesRead < sequenceNumber)
            {
                entry = new ReadOnlySpan<byte>(imageData, offset, 32);
                firstByte = entry[0];

                // Stop at empty or deleted entry
                if (firstByte == 0x00 || firstByte == 0xE5)
                    break;

                // Stop if not an LFN entry
                if (!IsLfNEntry(entry))
                    break;

                var (_, actualChecksum) = (GetLfNSequenceNumber(firstByte), entry[13]);

                // Verify checksum on the first LFN entry (the one closest to short name)
                if (lfnEntriesRead == 0 && actualChecksum != expectedChecksum)
                {
                    // Checksum mismatch - LFN is corrupted, fall back to short name
                    return null;
                }

                lfnParts.Add(DecodeLfNEntry(entry));
                lfnEntriesRead++;

                // Move to previous entry
                offset -= 32;

                // Make sure we don't go out of bounds
                if (offset < 0 || offset + 32 > imageData.Length)
                    break;
            }

            // LFN parts were read in reverse order, so reverse them back
            lfnParts.Reverse();

            // Combine all parts
            var fullLfN = string.Join("", lfnParts);

            return string.IsNullOrEmpty(fullLfN) ? null : fullLfN;
        }

        /// <summary>
        /// Checks if a filename needs LFN entries (is longer than 8.3 or contains special characters).
        /// </summary>
        private static bool NeedsLfNEntry(string fileName)
        {
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

            // Check if it exceeds 8.3 limits
            if (namePart.Length > 8 || extPart.Length > 3)
                return true;

            // Check for characters that require LFN (lowercase, special chars, Unicode)
            foreach (char c in fileName)
            {
                if (c < 0x20 || c > 0x7E || char.IsLower(c))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the number of LFN entries needed for a filename.
        /// </summary>
        private static int GetLfnEntryCount(string fileName)
        {
            if (!NeedsLfNEntry(fileName))
                return 0;
            return (fileName.Length + LFN_CHARS_PER_ENTRY - 1) / LFN_CHARS_PER_ENTRY;
        }

        /// <summary>
        /// Encodes a filename to FAT 8.3 format (uppercase, space-padded).
        /// </summary>
        private static string EncodeFileName(string fileName)
        {
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

            // Truncate or pad name part to 8 characters
            if (namePart.Length > 8)
                namePart = namePart.Substring(0, 8);
            namePart = namePart.PadRight(8, ' ').ToUpperInvariant();

            // Truncate or pad extension to 3 characters
            if (extPart.Length > 3)
                extPart = extPart.Substring(0, 3);
            extPart = extPart.PadRight(3, ' ').ToUpperInvariant();

            return namePart + extPart;
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
            var dataStartSector = GetDataStartSector(bpb);

            var sectorsPerCluster = bpb.SectorsPerCluster;
            var clusterOffset = (cluster - 2) * sectorsPerCluster;

            return dataStartSector + clusterOffset;
        }

        public void ExtractFolder(string sourcePath, string hostDestinationPath)
            => ExtractFolderInternal(sourcePath, hostDestinationPath, null);

        public Task ExtractFolderAsync(string sourcePath, string hostDestinationPath, IProgress<OperationProgress>? progress = null)
            => Task.Run(() => ExtractFolderInternal(sourcePath, hostDestinationPath, progress));

        private void ExtractFolderInternal(string sourcePath, string hostDestinationPath, IProgress<OperationProgress>? progress)
        {
            if (_imageData == null || !_isLoaded)
                throw new InvalidOperationException("Image must be opened before extracting.");

            System.IO.Directory.CreateDirectory(hostDestinationPath);

            var prefix = string.IsNullOrEmpty(sourcePath) ? string.Empty : sourcePath + "\\";
            var entriesToExtract = FilesAndFolders
                .Where(entry => string.IsNullOrEmpty(sourcePath) || !entry.Path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                .Where(entry => string.IsNullOrEmpty(prefix) || entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(entry => (
                    Entry: entry,
                    RelativePath: string.IsNullOrEmpty(prefix) ? entry.Path : entry.Path.Substring(prefix.Length)))
                .Where(item => !string.IsNullOrEmpty(item.RelativePath))
                .ToList();
            var totalBytes = entriesToExtract.Sum(item => item.Entry.Size ?? 0L);
            var extractedBytes = 0L;

            progress?.Report(new OperationProgress(0, totalBytes, string.Empty));

            foreach (var item in entriesToExtract)
            {
                var entry = item.Entry;
                var destFullPath = System.IO.Path.Combine(hostDestinationPath, item.RelativePath);

                if (entry.Size == null)
                {
                    System.IO.Directory.CreateDirectory(destFullPath);
                    continue;
                }

                progress?.Report(new OperationProgress(extractedBytes, totalBytes, item.RelativePath));

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

                extractedBytes += entry.Size ?? 0L;
                progress?.Report(new OperationProgress(extractedBytes, totalBytes, item.RelativePath));
            }
        }

        public byte[]? GetFileContent(string path)
        {
            if (_imageData == null || !_isLoaded)
                return null;

            var partitionByteOffset = _partitionStartSector * 512;
            var bootSector = new ReadOnlySpan<byte>(_imageData, (int)partitionByteOffset, 512);
            if (!IsFatBootSector(bootSector))
                return null;

            var bpb = ParseBpb(bootSector);

            var lastSlash = path.LastIndexOf('\\');
            var targetDirectory = lastSlash >= 0 ? path.Substring(0, lastSlash) : string.Empty;
            var name = lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;

            if (!TryResolveDirectory(targetDirectory, bpb, out var directory))
                return null;

            return ReadFileContent(directory, name, bpb);
        }

        private byte[]? ReadFileContent(DirectoryLocation directory, string fileName, Bpb bpb)
        {
            var imageData = _imageData!;

            foreach (var offset in EnumerateDirectoryEntryOffsets(directory, bpb))
            {
                var entry = new ReadOnlySpan<byte>(imageData, offset, 32);
                var firstByte = entry[0];
                if (firstByte == 0x00)
                    break;

                if (IsDeletedEntry(firstByte))
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

            foreach (var cluster in EnumerateClusterChain(firstCluster, bpb))
            {
                var sector = ClusterToSector(cluster, bpb);
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

                if (result.Count >= fileSize)
                    break;
            }

            return result.ToArray();
        }

        /// <summary>Returns the total usable byte capacity available for file data on the disk image.</summary>
        public long GetTotalBytes()
        {
            if (_imageData == null || !_isLoaded)
                return 0;

            var partitionByteOffset = _partitionStartSector * 512;
            var bpb = ParseBpb(new ReadOnlySpan<byte>(_imageData, (int)partitionByteOffset, 512));
            return (long)bpb.ClusterCount * bpb.BytesPerSector * bpb.SectorsPerCluster;
        }

        /// <summary>Returns an estimate of free bytes remaining on the disk image.</summary>
        public long GetFreeBytes()
        {
            if (_imageData == null || !_isLoaded)
                return 0;

            var partitionByteOffset = _partitionStartSector * 512;
            var bpb = ParseBpb(new ReadOnlySpan<byte>(_imageData, (int)partitionByteOffset, 512));
            long clusterSize = bpb.BytesPerSector * bpb.SectorsPerCluster;
            long freeBytes = 0;

            // Count free FAT entries (cluster 2 onwards)
            // FAT starts at: partition byte offset + (reserved sectors * bytes per sector)
            var fatOffset = (int)(partitionByteOffset + bpb.ReservedSectors * bpb.BytesPerSector);

            for (uint cluster = 2; cluster < bpb.ClusterCount + 2; cluster++)
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
            var partitionByteOffset = _partitionStartSector * 512;
            var bpb = ParseBpb(new ReadOnlySpan<byte>(_imageData!, (int)partitionByteOffset, 512));
            if (!TryResolveDirectory(targetDirectory, bpb, out var currentDirectory))
                throw new InvalidOperationException($"Directory '{targetDirectory}' not found on the disk image.");

            // Allocate 1 cluster for the directory
            var firstCluster = AllocateClusterChain(1, bpb);
            try
            {
                // Clear the cluster (fill with zeroes to avoid garbage entries)
                var clusterBytes = new byte[bpb.BytesPerSector * bpb.SectorsPerCluster];
                WriteFileToCluster(firstCluster, clusterBytes, bpb);

                var parentCluster = currentDirectory.FirstCluster;

                // Write 'this' and 'parent' directory entries into the new folder
                var newFolderDirectory = GetSubdirectoryLocation(firstCluster, bpb);
                WriteDotEntries(newFolderDirectory, firstCluster, parentCluster, bpb);

                // Create the directory entry in the parent directory
                CreateDirectoryEntry(currentDirectory, folderName, 0, firstCluster, true, bpb);
            }
            catch
            {
                FreeClusterChain(firstCluster, bpb);
                throw;
            }
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

        /// <summary>
        /// Updates the content of an existing file in the disk image.
        /// </summary>
        /// <param name="path">Full path to the file inside the image.</param>
        /// <param name="content">New raw file bytes to write.</param>
        public void UpdateFile(string path, byte[] content)
        {
            if (_imageData == null || !_isLoaded)
                throw new InvalidOperationException("Image must be opened before updating files.");

            var partitionByteOffset = _partitionStartSector * 512;
            var bpb = ParseBpb(new ReadOnlySpan<byte>(_imageData!, (int)partitionByteOffset, 512));

            var lastSlash = path.LastIndexOf('\\');
            var targetDirectory = lastSlash >= 0 ? path.Substring(0, lastSlash) : string.Empty;
            var fileName = lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;

            if (!TryResolveDirectory(targetDirectory, bpb, out var currentDirectory))
                throw new InvalidOperationException($"Directory '{targetDirectory}' not found on the disk image.");

            // Find the existing file entry and capture its current chain.
            var entryOffset = -1;
            uint oldFirstCluster = 0;
            foreach (var offset in EnumerateDirectoryEntryOffsets(currentDirectory, bpb))
            {
                var entry = new ReadOnlySpan<byte>(_imageData!, offset, 32);
                var firstByte = entry[0];
                if (firstByte == 0x00)
                    break;

                if (IsDeletedEntry(firstByte))
                    continue;

                var entryName = DecodeEntryName(entry);
                if (entryName == "." || entryName == "..") continue;
                if ((entry[11] & 0x08) != 0) continue; // volume label
                if (IsDirectoryEntry(entry)) continue;

                if (entryName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    entryOffset = offset;
                    oldFirstCluster = GetFirstCluster(entry);
                    break;
                }
            }

            if (entryOffset < 0)
                throw new InvalidOperationException($"File '{fileName}' not found.");

            var oldClusters = oldFirstCluster == 0
                ? new List<uint>()
                : EnumerateClusterChain(oldFirstCluster, bpb).ToList();
            var requiredClusters = GetRequiredClusterCount(content.Length, bpb);
            var additionalClusters = new List<uint>();
            var newFirstCluster = requiredClusters == 0 ? 0u : oldFirstCluster;

            if (requiredClusters > oldClusters.Count)
            {
                additionalClusters = FindFreeClusters(requiredClusters - (uint)oldClusters.Count, bpb);
                if (additionalClusters.Count != requiredClusters - oldClusters.Count)
                    throw new InvalidOperationException("Not enough free clusters available.");

                if (oldClusters.Count == 0)
                {
                    LinkClusterChain(additionalClusters, bpb);
                    newFirstCluster = additionalClusters[0];
                }
                else
                {
                    SetFatEntry(oldClusters[^1], additionalClusters[0], bpb);
                    LinkClusterChain(additionalClusters, bpb);
                }
            }

            try
            {
                WriteFileToCluster(newFirstCluster, content, bpb);
                UpdateDirectoryEntry(entryOffset, (uint)content.Length, newFirstCluster);

                if (requiredClusters == 0)
                {
                    FreeClusters(oldClusters, bpb);
                }
                else if (requiredClusters < oldClusters.Count)
                {
                    SetFatEntry(oldClusters[(int)requiredClusters - 1], GetEndOfChainMarker(bpb), bpb);
                    FreeClusters(oldClusters.Skip((int)requiredClusters), bpb);
                }
            }
            catch
            {
                if (oldClusters.Count == 0 && additionalClusters.Count > 0)
                {
                    FreeClusters(additionalClusters, bpb);
                }
                else if (oldClusters.Count > 0 && additionalClusters.Count > 0)
                {
                    SetFatEntry(oldClusters[^1], GetEndOfChainMarker(bpb), bpb);
                    FreeClusters(additionalClusters, bpb);
                }

                throw;
            }

            ParseFatFilesystem();
        }

        private void AddFileInternal(string targetDirectory, string fileName, byte[] content)
        {
            var partitionByteOffset = _partitionStartSector * 512;
            var bpb = ParseBpb(new ReadOnlySpan<byte>(_imageData!, (int)partitionByteOffset, 512));
            if (!TryResolveDirectory(targetDirectory, bpb, out var currentDirectory))
                throw new InvalidOperationException($"Directory '{targetDirectory}' not found on the disk image.");

            uint firstCluster = 0;
            try
            {
                // Allocate cluster chain for the file content
                firstCluster = AllocateClusterChain(content.Length, bpb);

                // Write file content to allocated clusters
                WriteFileToCluster(firstCluster, content, bpb);

                // CreateDirectoryEntry will determine if LFN is needed and handle encoding
                CreateDirectoryEntry(currentDirectory, fileName, (uint)content.Length, firstCluster, false, bpb);
            }
            catch
            {
                if (firstCluster != 0)
                    FreeClusterChain(firstCluster, bpb);

                throw;
            }
        }

        public void AddHostDirectory(string targetDirectory, string hostFolderPath)
        {
            if (_imageData == null || !_isLoaded)
                throw new InvalidOperationException("Image must be opened before adding folders.");

            AddHostDirectoryRecursive(targetDirectory, hostFolderPath, null);
            ParseFatFilesystem();
        }

        public Task AddHostDirectoryAsync(string targetDirectory, string hostFolderPath, long totalBytes, IProgress<OperationProgress>? progress = null)
            => Task.Run(() =>
            {
                if (_imageData == null || !_isLoaded)
                    throw new InvalidOperationException("Image must be opened before adding folders.");

                var progressTracker = new OperationProgressTracker(hostFolderPath, totalBytes, progress);
                AddHostDirectoryRecursive(targetDirectory, hostFolderPath, progressTracker);
                ParseFatFilesystem();
                progressTracker.ReportCompleted();
            });

        private void AddHostDirectoryRecursive(string targetDirectory, string hostFolderPath, OperationProgressTracker? progressTracker)
        {
            var dirInfo = new DirectoryInfo(hostFolderPath);

            CreateFolderInternal(targetDirectory, dirInfo.Name);

            var newTargetDir = string.IsNullOrEmpty(targetDirectory) ? dirInfo.Name : $"{targetDirectory}\\{dirInfo.Name}";

            foreach (var file in dirInfo.GetFiles())
            {
                progressTracker?.ReportCurrent(file.FullName);
                var content = File.ReadAllBytes(file.FullName);
                AddFileInternal(newTargetDir, file.Name, content);
                progressTracker?.Advance(file.Length, file.FullName);
            }

            foreach (var subDir in dirInfo.GetDirectories())
            {
                AddHostDirectoryRecursive(newTargetDir, subDir.FullName, progressTracker);
            }
        }

        /// <summary>
        /// Searches a directory region for a subdirectory with the given name and returns its first cluster.
        /// Returns 0 if not found.
        /// </summary>
        private uint FindDirectoryCluster(DirectoryLocation directory, string name, Bpb bpb)
        {
            var imageData = _imageData!;

            foreach (var offset in EnumerateDirectoryEntryOffsets(directory, bpb))
            {
                var entry = new ReadOnlySpan<byte>(imageData, offset, 32);
                var firstByte = entry[0];
                if (firstByte == 0x00)
                    break;

                if (IsDeletedEntry(firstByte))
                    continue;

                if (IsLfNEntry(entry))
                    continue;

                var shortName = DecodeEntryName(entry);
                if (shortName == "." || shortName == "..") continue;
                if ((entry[11] & 0x08) != 0) continue; // volume label

                // Try to get LFN name for comparison
                var longName = ReadLongFileName(offset, bpb);
                var entryName = longName ?? shortName;

                if (IsDirectoryEntry(entry) && entryName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return GetFirstCluster(entry);
            }

            return 0;
        }

        private uint AllocateClusterChain(int fileSize, Bpb bpb)
        {
            var clustersNeeded = GetRequiredClusterCount(fileSize, bpb);
            if (clustersNeeded == 0)
                return 0;

            var freeClusters = FindFreeClusters(clustersNeeded, bpb);
            if (freeClusters.Count != clustersNeeded)
                throw new InvalidOperationException("Not enough free clusters available.");

            LinkClusterChain(freeClusters, bpb);
            return freeClusters[0];
        }

        private uint GetFatEntry(uint cluster, Bpb bpb)
        {
            var fatStartSector = _partitionStartSector + bpb.ReservedSectors;
            var fatOffset = (int)(fatStartSector * bpb.BytesPerSector);

            if (IsFat12(bpb))
            {
                // FAT12
                var entryOffset = (int)(cluster * 3 / 2);
                var fatEntryStart = fatOffset + entryOffset;

                if (fatEntryStart + 2 > _imageData!.Length)
                    return GetEndOfChainMarker(bpb);

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
                    return GetEndOfChainMarker(bpb);

                return BitConverter.ToUInt16(_imageData.AsSpan(fatEntryStart, 2));
            }
        }

        private void SetFatEntry(uint cluster, uint value, Bpb bpb)
        {
            var fatStartSector = _partitionStartSector + bpb.ReservedSectors;
            var sectorsPerFat = bpb.SectorsPerFat == 0 ? bpb.SectorsPerFat16 : bpb.SectorsPerFat;
            var fatSizeBytes = (int)(sectorsPerFat * bpb.BytesPerSector);

            // Update all FAT copies to keep them identical
            for (uint fatIndex = 0; fatIndex < bpb.NumFats; fatIndex++)
            {
                var fatOffset = (int)(fatStartSector * bpb.BytesPerSector) + (int)(fatIndex * fatSizeBytes);

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
                        // Preserve the neighboring even cluster's low nibble and replace the odd entry.
                        existingValue &= 0x000F;
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
        }

        private void WriteFileToCluster(uint firstCluster, byte[] content, Bpb bpb)
        {
            if (content.Length == 0)
                return;

            if (!IsDataCluster(firstCluster, bpb))
                throw new InvalidOperationException("File content requires at least one allocated data cluster.");

            var offset = 0;

            foreach (var cluster in EnumerateClusterChain(firstCluster, bpb))
            {
                var sector = ClusterToSector(cluster, bpb);
                var sectorStart = (int)(sector * bpb.BytesPerSector);
                var clusterSize = (int)(bpb.BytesPerSector * bpb.SectorsPerCluster);
                if (sectorStart + clusterSize > _imageData!.Length)
                    throw new InvalidOperationException("Cluster chain points outside the image bounds.");

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

                if (offset >= content.Length)
                    break;
            }

            if (offset < content.Length)
                throw new InvalidOperationException("Cluster chain is shorter than the file content being written.");
        }

        private void CreateDirectoryEntry(DirectoryLocation directory, string fileName, uint fileSize, uint firstCluster, bool isDirectory, Bpb bpb)
        {
            var imageData = _imageData!;

            // Check if we need LFN entries for this filename
            int lfnEntriesNeeded = NeedsLfNEntry(fileName) ? GetLfnEntryCount(fileName) : 0;
            int totalEntriesNeeded = lfnEntriesNeeded + 1;

            // Find free entries
            int freeStartOffset = -1;
            int consecutiveFree = 0;

            foreach (var offset in EnumerateDirectoryEntryOffsets(directory, bpb))
            {
                var entry = imageData.AsSpan(offset, 32);
                var firstByte = entry[0];

                if (firstByte == 0x00 || firstByte == 0xE5)
                {
                    // Found a free entry (empty or deleted)
                    if (consecutiveFree == 0)
                        freeStartOffset = offset;
                    consecutiveFree++;

                    if (consecutiveFree >= totalEntriesNeeded)
                    {
                        // Found enough consecutive free entries
                        WriteEntryChain(freeStartOffset, fileName, fileSize, firstCluster, isDirectory, lfnEntriesNeeded);
                        return;
                    }
                }
                else
                {
                    // Active entry - reset counter unless this is an LFN entry (which can be reused)
                    if (!IsLfNEntry(entry))
                    {
                        consecutiveFree = 0;
                        freeStartOffset = -1;
                    }
                }
            }

            if (directory.FirstCluster == 0)
                throw new InvalidOperationException("No free directory entries available.");

            // Expand directory and try again
            var expandedCluster = ExpandDirectory(directory, bpb);
            var expandedOffset = (int)(ClusterToSector(expandedCluster, bpb) * bpb.BytesPerSector);
            WriteEntryChain(expandedOffset, fileName, fileSize, firstCluster, isDirectory, lfnEntriesNeeded);
        }

        /// <summary>
        /// Writes the "." and ".." entries for a newly created directory.
        /// </summary>
        private void WriteDotEntries(DirectoryLocation directory, uint thisCluster, uint parentCluster, Bpb bpb)
        {
            var imageData = _imageData!;
            var entrySize = 32;

            // "." entry - points to this directory
            var dotOffset = (int)(directory.StartSector * bpb.BytesPerSector);
            Array.Clear(imageData, dotOffset, entrySize);
            imageData[dotOffset] = (byte)'.';
            for (int i = 1; i < 8; i++) imageData[dotOffset + i] = (byte)' ';
            imageData[dotOffset + 11] = 0x10; // Directory attribute
            imageData[dotOffset + 26] = (byte)(thisCluster & 0xFF);
            imageData[dotOffset + 27] = (byte)((thisCluster >> 8) & 0xFF);

            // ".." entry - points to parent directory
            var dotDotOffset = dotOffset + entrySize;
            Array.Clear(imageData, dotDotOffset, entrySize);
            imageData[dotDotOffset] = (byte)'.';
            imageData[dotDotOffset + 1] = (byte)'.';
            for (int i = 2; i < 8; i++) imageData[dotDotOffset + i] = (byte)' ';
            imageData[dotDotOffset + 11] = 0x10; // Directory attribute
            imageData[dotDotOffset + 26] = (byte)(parentCluster & 0xFF);
            imageData[dotDotOffset + 27] = (byte)((parentCluster >> 8) & 0xFF);
        }

        /// <summary>
        /// Writes LFN entries (if needed) followed by the short name entry.
        /// </summary>
        private void WriteEntryChain(int offset, string fileName, uint fileSize, uint firstCluster, bool isDirectory, int lfnEntriesNeeded)
        {
            var imageData = _imageData!;

            if (lfnEntriesNeeded > 0)
            {
                // Write LFN entries first
                var shortName = EncodeFileName(fileName);
                var shortNameBytes = System.Text.Encoding.ASCII.GetBytes(shortName);
                WriteLfNEntries(offset, fileName, shortNameBytes, lfnEntriesNeeded);

                // Write short name entry after LFN entries
                var shortNameOffset = offset + (lfnEntriesNeeded * 32);
                WriteDirectoryEntry(shortNameOffset, shortName, fileSize, firstCluster, isDirectory);
            }
            else
            {
                // Just write the short name entry directly
                var shortName = EncodeFileName(fileName);
                WriteDirectoryEntry(offset, shortName, fileSize, firstCluster, isDirectory);
            }
        }

        /// <summary>
        /// Writes LFN entries for a long filename.
        /// Entries are written in forward order (entry 1 first, entry N last, closest to short name).
        /// </summary>
        private void WriteLfNEntries(int offset, string longName, byte[] shortNameBytes, int lfnEntriesNeeded)
        {
            var imageData = _imageData!;
            byte checksum = CalculateChecksum(shortNameBytes.AsSpan());

            for (int entryNum = 1; entryNum <= lfnEntriesNeeded; entryNum++)
            {
                int entryOffset = offset + ((entryNum - 1) * 32);

                // Clear the entry
                Array.Clear(imageData, entryOffset, 32);

                // Set sequence number (bit 6 set for last entry, closest to short name)
                byte sequenceByte = (byte)entryNum;
                if (entryNum == lfnEntriesNeeded)
                    sequenceByte |= 0x40; // Last LFN entry flag
                imageData[entryOffset] = sequenceByte;

                // Set attribute to LFN (0x0F)
                imageData[entryOffset + 11] = ATTR_LFN;

                // Calculate which characters go in this entry
                int charStart = (entryNum - 1) * LFN_CHARS_PER_ENTRY;

                // Part 1: 5 characters at offsets 0-9
                for (int i = 0; i < LFN_NAME_PART1_SIZE; i++)
                {
                    int charIdx = charStart + i;
                    if (charIdx < longName.Length)
                    {
                        var bytes = BitConverter.GetBytes((ushort)longName[charIdx]);
                        imageData[entryOffset + i * 2] = bytes[0];
                        imageData[entryOffset + i * 2 + 1] = bytes[1];
                    }
                    else
                    {
                        imageData[entryOffset + i * 2] = 0xFF;
                        imageData[entryOffset + i * 2 + 1] = 0xFF;
                    }
                }

                // Part 2: 6 characters at offsets 14-25
                for (int i = 0; i < LFN_NAME_PART2_SIZE; i++)
                {
                    int charIdx = charStart + LFN_NAME_PART1_SIZE + i;
                    if (charIdx < longName.Length)
                    {
                        var bytes = BitConverter.GetBytes((ushort)longName[charIdx]);
                        imageData[entryOffset + 14 + i * 2] = bytes[0];
                        imageData[entryOffset + 14 + i * 2 + 1] = bytes[1];
                    }
                    else
                    {
                        imageData[entryOffset + 14 + i * 2] = 0xFF;
                        imageData[entryOffset + 14 + i * 2 + 1] = 0xFF;
                    }
                }

                // Part 3: 2 characters at offsets 28-31
                for (int i = 0; i < LFN_NAME_PART3_SIZE; i++)
                {
                    int charIdx = charStart + LFN_NAME_PART1_SIZE + LFN_NAME_PART2_SIZE + i;
                    if (charIdx < longName.Length)
                    {
                        var bytes = BitConverter.GetBytes((ushort)longName[charIdx]);
                        imageData[entryOffset + 28 + i * 2] = bytes[0];
                        imageData[entryOffset + 28 + i * 2 + 1] = bytes[1];
                    }
                    else
                    {
                        imageData[entryOffset + 28 + i * 2] = 0xFF;
                        imageData[entryOffset + 28 + i * 2 + 1] = 0xFF;
                    }
                }

                // Set checksum at offset 13
                imageData[entryOffset + 13] = checksum;
            }
        }

        private void WriteDirectoryEntry(int offset, string fileName, uint fileSize, uint firstCluster, bool isDirectory)
        {
            var imageData = _imageData!;

            Array.Clear(imageData, offset, 32);

            for (int j = 0; j < 11 && j < fileName.Length; j++)
            {
                imageData[offset + j] = (byte)fileName[j];
            }

            imageData[offset + 11] = isDirectory ? (byte)0x10 : (byte)0x00;

            var now = DateTime.Now;
            ushort fatTime = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
            ushort fatDate = (ushort)(((Math.Max(1980, now.Year) - 1980) << 9) | (now.Month << 5) | now.Day);

            var timeBytes = BitConverter.GetBytes(fatTime);
            imageData[offset + 22] = timeBytes[0];
            imageData[offset + 23] = timeBytes[1];

            var dateBytes = BitConverter.GetBytes(fatDate);
            imageData[offset + 24] = dateBytes[0];
            imageData[offset + 25] = dateBytes[1];

            var clusterBytes = BitConverter.GetBytes((ushort)firstCluster);
            imageData[offset + 26] = clusterBytes[0];
            imageData[offset + 27] = clusterBytes[1];

            var sizeBytes = BitConverter.GetBytes(fileSize);
            imageData[offset + 28] = sizeBytes[0];
            imageData[offset + 29] = sizeBytes[1];
            imageData[offset + 30] = sizeBytes[2];
            imageData[offset + 31] = sizeBytes[3];
        }
        public void DeleteEntry(string path)
        {
            if (_imageData == null || !_isLoaded)
                throw new InvalidOperationException("Image must be opened before deleting files.");

            var partitionByteOffset = _partitionStartSector * 512;
            var bpb = ParseBpb(new ReadOnlySpan<byte>(_imageData, (int)partitionByteOffset, 512));

            var lastSlash = path.LastIndexOf('\\');
            var targetDirectory = lastSlash >= 0 ? path.Substring(0, lastSlash) : string.Empty;
            var name = lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;

            if (!TryResolveDirectory(targetDirectory, bpb, out var currentDirectory))
                throw new FileNotFoundException($"Directory '{targetDirectory}' not found on the disk image.");

            DeleteEntryInternal(currentDirectory, name, bpb);

            ParseFatFilesystem();
        }

        public void RenameEntry(string path, string newName)
        {
            if (_imageData == null || !_isLoaded)
                throw new InvalidOperationException("Image must be opened before renaming files.");

            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("New name cannot be empty.");

            var partitionByteOffset = _partitionStartSector * 512;
            var bpb = ParseBpb(new ReadOnlySpan<byte>(_imageData, (int)partitionByteOffset, 512));

            var lastSlash = path.LastIndexOf('\\');
            var targetDirectory = lastSlash >= 0 ? path.Substring(0, lastSlash) : string.Empty;
            var oldName = lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;

            if (!TryResolveDirectory(targetDirectory, bpb, out var currentDirectory))
                throw new FileNotFoundException($"Directory '{targetDirectory}' not found on the disk image.");

            RenameEntryInternal(currentDirectory, oldName, newName, bpb);

            ParseFatFilesystem();
        }

        /// <summary>
        /// Moves a file to a different directory within the disk image.
        /// </summary>
        /// <param name="sourcePath">Full path to the source file inside the image.</param>
        /// <param name="targetDirectory">Target directory path (empty string for root).</param>
        public void MoveEntry(string sourcePath, string targetDirectory)
        {
            if (_imageData == null || !_isLoaded)
                throw new InvalidOperationException("Image must be opened before moving files.");

            var lastSlash = sourcePath.LastIndexOf('\\');
            var sourceDirectory = lastSlash >= 0 ? sourcePath.Substring(0, lastSlash) : string.Empty;
            var fileName = lastSlash >= 0 ? sourcePath.Substring(lastSlash + 1) : sourcePath;

            // If moving within the same directory, just rename
            if (sourceDirectory.Equals(targetDirectory, StringComparison.OrdinalIgnoreCase))
            {
                RenameEntry(sourcePath, fileName);
                return;
            }

            // Read file content
            var content = GetFileContent(sourcePath);
            if (content == null)
                throw new FileNotFoundException($"File '{sourcePath}' not found in the disk image.");

            // Delete original entry
            DeleteEntry(sourcePath);

            // Add file to new location
            AddFileInternal(targetDirectory, fileName, content);

            ParseFatFilesystem();
        }

        private void DeleteEntryInternal(DirectoryLocation directory, string name, Bpb bpb)
        {
            var imageData = _imageData!;

            System.Diagnostics.Debug.WriteLine($"DeleteEntryInternal: looking for '{name}' in directory starting at sector {directory.StartSector}");

            foreach (var offset in EnumerateDirectoryEntryOffsets(directory, bpb))
            {
                var entry = new ReadOnlySpan<byte>(imageData, offset, 32);
                var firstByte = entry[0];

                if (firstByte == 0x00)
                    break;

                if (IsDeletedEntry(firstByte))
                    continue;

                // Skip LFN entries - they'll be processed with the short name entry
                if (IsLfNEntry(entry))
                    continue;

                var entryName = DecodeEntryName(entry);

                System.Diagnostics.Debug.WriteLine($"  Entry '{entryName}' (firstByte=0x{firstByte:X2}, attr=0x{entry[11]:X2})");

                // Skip volume label entries
                if ((entry[11] & 0x08) != 0)
                    continue;

                // Skip special entries
                if (entryName == "." || entryName == "..")
                    continue;

                // Try to get LFN name for comparison
                var longName = ReadLongFileName(offset, bpb);
                var compareName = longName ?? entryName;

                // Check if this is the entry we're looking for
                var matches = compareName.Equals(name, StringComparison.OrdinalIgnoreCase);
                System.Diagnostics.Debug.WriteLine($"    Comparing '{compareName}' with '{name}' -> {matches}");
                if (matches)
                {
                    var firstCluster = GetFirstCluster(entry);
                    if (IsDirectoryEntry(entry))
                    {
                        if (IsDataCluster(firstCluster, bpb))
                        {
                            DeleteDirectoryContents(GetSubdirectoryLocation(firstCluster, bpb), bpb);
                            FreeClusterChain(firstCluster, bpb);
                        }
                    }
                    else
                    {
                        FreeClusterChain(firstCluster, bpb);
                    }

                    // Count and delete LFN entries preceding this short name entry
                    int lfnCount = 0;
                    int lfnCheckOffset = offset - 32;
                    while (lfnCheckOffset >= 0)
                    {
                        var lfnEntry = new ReadOnlySpan<byte>(imageData, lfnCheckOffset, 32);
                        byte lfnFirstByte = lfnEntry[0];
                        if (lfnFirstByte == 0x00 || lfnFirstByte == 0xE5)
                            break;
                        if (IsLfNEntry(lfnEntry))
                        {
                            lfnCount++;
                            lfnCheckOffset -= 32;
                        }
                        else
                        {
                            break;
                        }
                    }

                    // Mark LFN entries as deleted
                    for (int i = 0; i < lfnCount; i++)
                    {
                        imageData[offset - ((lfnCount - i) * 32)] = 0xE5;
                    }

                    // Mark short name entry as deleted
                    imageData[offset] = 0xE5;

                    return;
                }
            }

            var availableEntries = new List<string>();
            foreach (var offset in EnumerateDirectoryEntryOffsets(directory, bpb))
            {
                var entry = new ReadOnlySpan<byte>(imageData, offset, 32);
                var firstByte = entry[0];
                if (firstByte == 0x00)
                    break;
                if (IsDeletedEntry(firstByte)) continue;
                if (IsLfNEntry(entry)) continue;
                if ((entry[11] & 0x08) != 0) continue;
                var entryName = DecodeEntryName(entry);
                if (entryName != "." && entryName != "..")
                    availableEntries.Add(entryName);
            }
            throw new FileNotFoundException($"Entry '{name}' not found. Available entries: [{string.Join(", ", availableEntries)}]");
        }

        private void RenameEntryInternal(DirectoryLocation directory, string oldName, string newName, Bpb bpb)
        {
            var imageData = _imageData!;

            System.Diagnostics.Debug.WriteLine($"RenameEntryInternal: renaming '{oldName}' to '{newName}' in directory starting at sector {directory.StartSector}");

            // Check if new name already exists
            foreach (var offset in EnumerateDirectoryEntryOffsets(directory, bpb))
            {
                var entry = new ReadOnlySpan<byte>(imageData, offset, 32);
                var firstByte = entry[0];

                if (firstByte == 0x00)
                    break;

                if (IsDeletedEntry(firstByte))
                    continue;

                if (IsLfNEntry(entry))
                    continue;

                if ((entry[11] & 0x08) != 0)
                    continue;

                var entryName = DecodeEntryName(entry);
                var longName = ReadLongFileName(offset, bpb);
                var compareName = longName ?? entryName;
                if (compareName != "." && compareName != ".." && compareName.Equals(newName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"An entry with the name '{newName}' already exists in this directory.");
                }
            }

            // Find and rename the entry
            foreach (var offset in EnumerateDirectoryEntryOffsets(directory, bpb))
            {
                var entry = new ReadOnlySpan<byte>(imageData, offset, 32);
                var firstByte = entry[0];

                if (firstByte == 0x00)
                    break;

                if (IsDeletedEntry(firstByte))
                    continue;

                if (IsLfNEntry(entry))
                    continue;

                if ((entry[11] & 0x08) != 0)
                    continue;

                var entryName = DecodeEntryName(entry);
                var longName = ReadLongFileName(offset, bpb);
                var compareName = longName ?? entryName;

                if (compareName.Equals(oldName, StringComparison.OrdinalIgnoreCase))
                {
                    // Count existing LFN entries
                    int lfnCount = 0;
                    int lfnCheckOffset = offset - 32;
                    while (lfnCheckOffset >= 0)
                    {
                        var lfnEntry = new ReadOnlySpan<byte>(imageData, lfnCheckOffset, 32);
                        byte lfnFirstByte = lfnEntry[0];
                        if (lfnFirstByte == 0x00 || lfnFirstByte == 0xE5)
                            break;
                        if (IsLfNEntry(lfnEntry))
                        {
                            lfnCount++;
                            lfnCheckOffset -= 32;
                        }
                        else
                        {
                            break;
                        }
                    }

                    // Calculate new LFN requirements
                    int newLfnCount = NeedsLfNEntry(newName) ? GetLfnEntryCount(newName) : 0;

                    if (newLfnCount != lfnCount)
                    {
                        throw new IOException($"Renaming '{oldName}' to '{newName}' would change the number of directory entries required. This operation is not supported. Delete and re-create the entry instead.");
                    }

                    if (newLfnCount > 0)
                    {
                        // Update LFN entries and short name
                        var newShortName = EncodeFileName(newName);
                        var shortNameBytes = System.Text.Encoding.ASCII.GetBytes(newShortName);
                        byte newChecksum = CalculateChecksum(shortNameBytes.AsSpan());

                        // Update LFN entries with new name and checksum
                        for (int i = 0; i < newLfnCount; i++)
                        {
                            int lfnOffset = offset - ((newLfnCount - i) * 32);
                            // Clear and update
                            Array.Clear(imageData, lfnOffset, 32);

                            // Set sequence number
                            byte seqNum = (byte)(i + 1);
                            if (i == newLfnCount - 1)
                                seqNum |= 0x40; // Last entry flag
                            imageData[lfnOffset] = seqNum;
                            imageData[lfnOffset + 11] = ATTR_LFN;

                            // Write characters for this entry
                            int charStart = i * LFN_CHARS_PER_ENTRY;

                            // Part 1: 5 chars at offsets 0-9
                            for (int j = 0; j < LFN_NAME_PART1_SIZE; j++)
                            {
                                int charIdx = charStart + j;
                                if (charIdx < newName.Length)
                                {
                                    var bytes = BitConverter.GetBytes((ushort)newName[charIdx]);
                                    imageData[lfnOffset + j * 2] = bytes[0];
                                    imageData[lfnOffset + j * 2 + 1] = bytes[1];
                                }
                                else
                                {
                                    imageData[lfnOffset + j * 2] = 0xFF;
                                    imageData[lfnOffset + j * 2 + 1] = 0xFF;
                                }
                            }

                            // Part 2: 6 chars at offsets 14-25
                            for (int j = 0; j < LFN_NAME_PART2_SIZE; j++)
                            {
                                int charIdx = charStart + LFN_NAME_PART1_SIZE + j;
                                if (charIdx < newName.Length)
                                {
                                    var bytes = BitConverter.GetBytes((ushort)newName[charIdx]);
                                    imageData[lfnOffset + 14 + j * 2] = bytes[0];
                                    imageData[lfnOffset + 14 + j * 2 + 1] = bytes[1];
                                }
                                else
                                {
                                    imageData[lfnOffset + 14 + j * 2] = 0xFF;
                                    imageData[lfnOffset + 14 + j * 2 + 1] = 0xFF;
                                }
                            }

                            // Part 3: 2 chars at offsets 28-31
                            for (int j = 0; j < LFN_NAME_PART3_SIZE; j++)
                            {
                                int charIdx = charStart + LFN_NAME_PART1_SIZE + LFN_NAME_PART2_SIZE + j;
                                if (charIdx < newName.Length)
                                {
                                    var bytes = BitConverter.GetBytes((ushort)newName[charIdx]);
                                    imageData[lfnOffset + 28 + j * 2] = bytes[0];
                                    imageData[lfnOffset + 28 + j * 2 + 1] = bytes[1];
                                }
                                else
                                {
                                    imageData[lfnOffset + 28 + j * 2] = 0xFF;
                                    imageData[lfnOffset + 28 + j * 2 + 1] = 0xFF;
                                }
                            }

                            imageData[lfnOffset + 13] = newChecksum;
                        }

                        // Update short name entry
                        var entryBuffer = new byte[32];
                        entry.CopyTo(entryBuffer);
                        for (int i = 0; i < 11 && i < newShortName.Length; i++)
                        {
                            entryBuffer[i] = (byte)newShortName[i];
                        }
                        entryBuffer.AsSpan().CopyTo(imageData.AsSpan(offset, 32));
                    }
                    else
                    {
                        // No LFN - just update the short name
                        var newShortName = EncodeFileName(newName);

                        // Clear old LFN entries if any
                        for (int i = 0; i < lfnCount; i++)
                        {
                            imageData[offset - ((lfnCount - i) * 32)] = 0xE5;
                        }

                        // Update short name entry
                        var entryBuffer = new byte[32];
                        entry.CopyTo(entryBuffer);
                        for (int i = 0; i < 11 && i < newShortName.Length; i++)
                        {
                            entryBuffer[i] = (byte)newShortName[i];
                        }
                        entryBuffer.AsSpan().CopyTo(imageData.AsSpan(offset, 32));
                    }

                    System.Diagnostics.Debug.WriteLine($"  Renamed '{oldName}' to '{newName}'");
                    return;
                }
            }

            throw new FileNotFoundException($"Entry '{oldName}' not found.");
        }

        private void FreeClusterChain(uint firstCluster, Bpb bpb)
        {
            if (!IsDataCluster(firstCluster, bpb))
                return;

            foreach (var cluster in EnumerateClusterChain(firstCluster, bpb).ToList())
            {
                SetFatEntry(cluster, 0, bpb);
            }
        }

        private void DeleteDirectoryContents(DirectoryLocation directory, Bpb bpb)
        {
            var imageData = _imageData!;

            foreach (var offset in EnumerateDirectoryEntryOffsets(directory, bpb))
            {
                var entry = new ReadOnlySpan<byte>(imageData, offset, 32);
                var firstByte = entry[0];
                if (firstByte == 0x00)
                    break;

                if (IsDeletedEntry(firstByte))
                    continue;

                if ((entry[11] & 0x08) != 0)
                    continue;

                var entryName = DecodeEntryName(entry);
                if (entryName == "." || entryName == "..")
                    continue;

                var firstCluster = GetFirstCluster(entry);
                if (IsDirectoryEntry(entry))
                {
                    if (IsDataCluster(firstCluster, bpb))
                    {
                        DeleteDirectoryContents(GetSubdirectoryLocation(firstCluster, bpb), bpb);
                        FreeClusterChain(firstCluster, bpb);
                    }
                }
                else
                {
                    FreeClusterChain(firstCluster, bpb);
                }

                imageData[offset] = 0xE5;
            }
        }

        private uint GetNextCluster(uint currentCluster, Bpb bpb)
        {
            // Calculate FAT entry offset (FAT12 uses 1.5 bytes per entry, FAT16 uses 2 bytes)
            var fatStartSector = _partitionStartSector + bpb.ReservedSectors;
            var fatOffset = (int)(fatStartSector * bpb.BytesPerSector);

            System.Diagnostics.Debug.WriteLine($"    GetNextCluster: cluster={currentCluster}, fatStartSector={fatStartSector}, fatOffset={fatOffset}");

            // For FAT12: each entry is 1.5 bytes
            if (IsFat12(bpb))
            {
                var entryOffset = (int)(currentCluster * 3 / 2);
                var fatEntryStart = fatOffset + entryOffset;

                if (fatEntryStart + 2 > _imageData!.Length)
                {
                    System.Diagnostics.Debug.WriteLine($"    FAT12: out of bounds, returning 0");
                    return 0;
                }

                var fatValue = (ushort)(_imageData[fatEntryStart] | (_imageData[fatEntryStart + 1] << 8));
                System.Diagnostics.Debug.WriteLine($"    FAT12: entryOffset={entryOffset}, fatEntryStart={fatEntryStart}, rawValue=0x{fatValue:X4}");

                if (currentCluster % 2 == 0)
                {
                    fatValue &= 0x0FFF;
                }
                else
                {
                    fatValue >>= 4;
                }

                System.Diagnostics.Debug.WriteLine($"    FAT12: final value=0x{fatValue:X4}");
                return (uint)fatValue;
            }
            else
            {
                // FAT16: each entry is 2 bytes
                var entryOffset = (int)(currentCluster * 2);
                var fatEntryStart = fatOffset + entryOffset;

                if (fatEntryStart + 2 > _imageData!.Length)
                {
                    System.Diagnostics.Debug.WriteLine($"    FAT16: out of bounds, returning 0");
                    return 0;
                }

                var fatValue = BitConverter.ToUInt16(_imageData.AsSpan(fatEntryStart, 2));
                System.Diagnostics.Debug.WriteLine($"    FAT16: entryOffset={entryOffset}, fatEntryStart={fatEntryStart}, value=0x{fatValue:X4}");
                return (uint)fatValue;
            }
        }

        public enum SectorType
        {
            BootSector,
            PartitionTable,
            FatTable,
            RootDirectory,
            Allocated,
            Free,
            Reserved
        }

        public class SectorMapInfo
        {
            public SectorType[] SectorTypes { get; set; } = Array.Empty<SectorType>();
            public int TotalSectors { get; set; }
            public int AllocatedSectors { get; set; }
            public int FreeSectors { get; set; }
            public int TotalAllocatedSectors { get; set; }
            public int TotalFreeSectors { get; set; }
            public int MaxSectorsToShow { get; set; } = 50000;
            public bool HasPartition { get; set; }
            public uint PartitionStartSector { get; set; }
            public uint BytesPerSector { get; set; } = 512;
            public byte SectorsPerCluster { get; set; }
        }

        public class ClusterMapInfo
        {
            public SectorType[] ClusterTypes { get; set; } = Array.Empty<SectorType>();
            public int TotalClusters { get; set; }
            public int AllocatedClusters { get; set; }
            public int FreeClusters { get; set; }
            public int TotalAllocatedClusters { get; set; }
            public int TotalFreeClusters { get; set; }
            public int MaxClustersToShow { get; set; } = 10000;
            public bool HasPartition { get; set; }
            public uint PartitionStartSector { get; set; }
            public uint BytesPerSector { get; set; } = 512;
            public byte SectorsPerCluster { get; set; }
            public int TotalSectors { get; set; }
        }

        public enum FatType
        {
            Fat12,
            Fat16,
            Fat32
        }

        /// <summary>
        /// Gets the FAT filesystem type of the loaded image, or null if not loaded.
        /// </summary>
        public FatType? FilesystemType => _fatType;

        private readonly struct DirectoryLocation(uint startSector, int numSectors, uint firstCluster)
        {
            public uint StartSector { get; } = startSector;
            public int NumSectors { get; } = numSectors;
            public uint FirstCluster { get; } = firstCluster;
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
            public uint ClusterCount { get; set; }
        }

        /// <summary>
        /// Creates a new blank FAT12 disk image with the specified size.
        /// </summary>
        /// <param name="imageSize">Total size of the image in bytes (must be a multiple of 512).</param>
        /// <param name="volumeLabel">Optional volume label (max 11 characters).</param>
        public void CreateBlankImage(long imageSize, string volumeLabel = "")
        {
            CreateBlankFloppy(imageSize, volumeLabel);
        }

        /// <summary>
        /// Creates a new blank FAT16 hard disk image with MBR partition table.
        /// </summary>
        /// <param name="cylinders">Number of cylinders (1-16383).</param>
        /// <param name="heads">Number of heads per cylinder (1-255).</param>
        /// <param name="sectorsPerTrack">Number of sectors per track (1-255).</param>
        /// <param name="volumeLabel">Optional volume label (max 11 characters).</param>
        public void CreateHardDiskImage(uint cylinders, uint heads, uint sectorsPerTrack, string volumeLabel = "")
        {
            if (cylinders == 0 || cylinders > 16383)
                throw new ArgumentException("Cylinders must be between 1 and 16383.");
            if (heads == 0 || heads > 255)
                throw new ArgumentException("Heads must be between 1 and 255.");
            if (sectorsPerTrack == 0 || sectorsPerTrack > 255)
                throw new ArgumentException("Sectors per track must be between 1 and 255.");

            if (!string.IsNullOrEmpty(volumeLabel) && volumeLabel.Length > 11)
                volumeLabel = volumeLabel.Substring(0, 11);

            uint bytesPerSector = 512;
            long imageSize = (long)cylinders * heads * sectorsPerTrack * bytesPerSector;
            _imageData = new byte[imageSize];
            _partitionStartSector = 63; // Standard: first partition starts at sector 63

            uint totalSectors = cylinders * heads * sectorsPerTrack;
            uint partitionSectors = totalSectors - _partitionStartSector;

            CreateMbr(_imageData.AsSpan(0, 512), cylinders, heads, sectorsPerTrack);
            var bpb = CreateFat16BootSector(_imageData.AsSpan((int)(_partitionStartSector * bytesPerSector), 512), partitionSectors, heads, sectorsPerTrack);
            InitializeFat16(bpb, _partitionStartSector, volumeLabel);

            _isLoaded = true;
            _fatType = FatType.Fat16;
            VolumeLabel = volumeLabel;
            FilesAndFolders.Clear();
        }

        private void CreateMbr(Span<byte> mbr, uint cylinders, uint heads, uint sectorsPerTrack)
        {
            // MBR boot code (446 bytes) - simple NOPs with a message
            for (int i = 0; i < 3; i++) mbr[i] = 0xEB; // JMP instruction

            // Partition table starts at offset 446
            // Create one primary partition spanning the entire disk

            var partitionEntry = mbr.Slice(446, 16);

            // Boot indicator (0x80 = active/bootable)
            partitionEntry[0] = 0x80;

            // CHS start location: head=0, sector=1, cylinder=0
            partitionEntry[1] = 0x00; // Head
            partitionEntry[2] = 0x01; // Sector (high 2 bits of cyl | sector)
            partitionEntry[3] = 0x00; // Cylinder

            // Partition type: 0x0E = FAT16 (>32MB, LBA), 0x06 for <32MB
            uint totalSectors = cylinders * heads * sectorsPerTrack;
            partitionEntry[4] = totalSectors > 65536 ? (byte)0x0E : (byte)0x06;

            // CHS end location
            byte endHead = (byte)(heads - 1);
            byte endSector = (byte)(0x3F | (((cylinders - 1) >> 2) & 0xC0));
            byte endCylinder = (byte)((cylinders - 1) & 0x3F);
            partitionEntry[5] = endHead;
            partitionEntry[6] = endSector;
            partitionEntry[7] = endCylinder;

            // Starting LBA sector (little-endian, 4 bytes)
            uint startLba = 63;
            partitionEntry[8] = (byte)(startLba & 0xFF);
            partitionEntry[9] = (byte)((startLba >> 8) & 0xFF);
            partitionEntry[10] = (byte)((startLba >> 16) & 0xFF);
            partitionEntry[11] = (byte)((startLba >> 24) & 0xFF);

            // Partition size in sectors (little-endian, 4 bytes)
            uint sectorCount = totalSectors - startLba;
            partitionEntry[12] = (byte)(sectorCount & 0xFF);
            partitionEntry[13] = (byte)((sectorCount >> 8) & 0xFF);
            partitionEntry[14] = (byte)((sectorCount >> 16) & 0xFF);
            partitionEntry[15] = (byte)((sectorCount >> 24) & 0xFF);

            // MBR signature at offset 510-511
            mbr[510] = 0x55;
            mbr[511] = 0xAA;
        }

        private Bpb CreateFat16BootSector(Span<byte> bootSector, uint partitionSectors, uint heads, uint sectorsPerTrack)
        {
            // Calculate FAT16 parameters
            byte sectorsPerCluster = GetSectorsPerClusterForFat16(partitionSectors);
            uint reservedSectors = 32; // Standard for FAT16 hard disks
            byte numFats = 2;
            uint rootDirEntries = 512; // Standard for FAT16

            uint bytesPerSector = 512;
            uint rootDirSectors = ((rootDirEntries * 32) + (bytesPerSector - 1)) / bytesPerSector;
            uint dataSectors = partitionSectors - reservedSectors - (numFats * CalculateFat16Sectors(partitionSectors, sectorsPerCluster, reservedSectors, numFats, rootDirEntries));

            // Recalculate to ensure consistency
            uint sectorsPerFat = CalculateFat16Sectors(partitionSectors, sectorsPerCluster, reservedSectors, numFats, rootDirEntries);
            uint dataStartSector = reservedSectors + (numFats * sectorsPerFat) + rootDirSectors;
            uint totalDataSectors = partitionSectors - dataStartSector;

            // Build boot sector
            // Jump instruction to skip BPB
            bootSector[0] = 0xEB;
            bootSector[1] = 0x76; // Jump offset (to boot code)
            bootSector[2] = 0x90; // NOP

            // OEM ID (8 bytes) - "MSDOS5.0" for FAT16
            var oemBytes = System.Text.Encoding.ASCII.GetBytes("MSDOS5.0");
            for (int i = 0; i < Math.Min(oemBytes.Length, 8); i++) bootSector[3 + i] = oemBytes[i];

            // Bytes per sector (offset 11, 2 bytes)
            bootSector[11] = (byte)(bytesPerSector & 0xFF);
            bootSector[12] = (byte)((bytesPerSector >> 8) & 0xFF);

            // Sectors per cluster (offset 13, 1 byte)
            bootSector[13] = sectorsPerCluster;

            // Reserved sectors (offset 14, 2 bytes)
            bootSector[14] = (byte)(reservedSectors & 0xFF);
            bootSector[15] = (byte)((reservedSectors >> 8) & 0xFF);

            // Number of FATs (offset 16, 1 byte)
            bootSector[16] = numFats;

            // Root directory entries (offset 17, 2 bytes)
            bootSector[17] = (byte)(rootDirEntries & 0xFF);
            bootSector[18] = (byte)((rootDirEntries >> 8) & 0xFF);

            // Total sectors (16-bit, offset 19, 2 bytes) - for FAT16 <= 32MB
            uint totalSectors16 = partitionSectors <= 65535 ? partitionSectors : 0;
            bootSector[19] = (byte)(totalSectors16 & 0xFF);
            bootSector[20] = (byte)((totalSectors16 >> 8) & 0xFF);

            // Media descriptor (offset 21, 1 byte) - 0xF8 for hard disk
            bootSector[21] = 0xF8;

            // Sectors per FAT (16-bit, offset 22, 2 bytes)
            bootSector[22] = (byte)(sectorsPerFat & 0xFF);
            bootSector[23] = (byte)((sectorsPerFat >> 8) & 0xFF);

            // Sectors per track (offset 24, 2 bytes)
            bootSector[24] = (byte)(sectorsPerTrack & 0xFF);
            bootSector[25] = (byte)((sectorsPerTrack >> 8) & 0xFF);

            // Heads per cylinder (offset 26, 2 bytes)
            bootSector[26] = (byte)(heads & 0xFF);
            bootSector[27] = (byte)((heads >> 8) & 0xFF);

            // Hidden sectors (offset 28, 4 bytes) - sectors before this partition
            uint hiddenSectors = _partitionStartSector;
            bootSector[28] = (byte)(hiddenSectors & 0xFF);
            bootSector[29] = (byte)((hiddenSectors >> 8) & 0xFF);
            bootSector[30] = (byte)((hiddenSectors >> 16) & 0xFF);
            bootSector[31] = (byte)((hiddenSectors >> 24) & 0xFF);

            // Total sectors (32-bit, offset 32, 4 bytes) - for >32MB partitions
            bootSector[32] = (byte)(partitionSectors & 0xFF);
            bootSector[33] = (byte)((partitionSectors >> 8) & 0xFF);
            bootSector[34] = (byte)((partitionSectors >> 16) & 0xFF);
            bootSector[35] = (byte)((partitionSectors >> 24) & 0xFF);

            // Boot signature (offset 510, 2 bytes)
            bootSector[510] = 0x55;
            bootSector[511] = 0xAA;

            // Return BPB struct for later use
            return new Bpb
            {
                BytesPerSector = 512,
                SectorsPerCluster = sectorsPerCluster,
                ReservedSectors = reservedSectors,
                NumFats = numFats,
                RootDirEntries = rootDirEntries,
                SectorsPerFat16 = sectorsPerFat,
                SectorsPerTrack = sectorsPerTrack,
                HeadsPerCylinder = heads,
                TotalSectors32 = partitionSectors
            };
        }

        private byte GetSectorsPerClusterForFat16(uint totalSectors)
        {
            // Choose sectors per cluster for FAT16 based on partition size
            if (totalSectors <= 32768) return 4;   // Up to ~16MB
            if (totalSectors <= 65536) return 8;   // Up to ~32MB
            if (totalSectors <= 131072) return 16; // Up to ~64MB
            if (totalSectors <= 262144) return 32; // Up to ~128MB
            return 64;                              // Larger partitions
        }

        private uint CalculateFat16Sectors(uint totalSectors, byte sectorsPerCluster, uint reservedSectors, byte numFats, uint rootDirEntries)
        {
            uint bytesPerSector = 512;
            uint rootDirSectors = ((rootDirEntries * 32) + (bytesPerSector - 1)) / bytesPerSector;

            // Iterate to find correct FAT size
            for (int i = 0; i < 10; i++)
            {
                uint fatSizeIter = (i > 0) ? ((totalSectors - reservedSectors - rootDirSectors) / sectorsPerCluster * 3u / 2 + bytesPerSector - 1) / bytesPerSector : 1;
                uint dataStartIter = reservedSectors + (numFats * fatSizeIter) + rootDirSectors;
                uint dataSectorsIter = totalSectors - dataStartIter;

                if (dataSectorsIter <= 0) break;

                uint clusterCountIter = dataSectorsIter / sectorsPerCluster;
                uint fatSizeNew = (clusterCountIter * 3u / 2 + bytesPerSector - 1) / bytesPerSector;

                // Check if converged
                uint nextDataStart = reservedSectors + (numFats * fatSizeNew) + rootDirSectors;
                if (Math.Abs((long)nextDataStart - (long)dataStartIter) < 2)
                    return fatSizeNew;
            }

            // Fallback calculation
            uint dataStartFallback = reservedSectors + rootDirSectors;
            uint dataSectorsFallback = totalSectors - dataStartFallback;
            uint clusterCountFallback = dataSectorsFallback / sectorsPerCluster;
            return (clusterCountFallback * 3u / 2 + bytesPerSector - 1) / bytesPerSector;
        }

        private void InitializeFat16(Bpb bpb, uint partitionStart, string volumeLabel)
        {
            var imageData = _imageData!;
            uint bytesPerSector = bpb.BytesPerSector;
            byte sectorsPerCluster = bpb.SectorsPerCluster;
            uint reservedSectors = bpb.ReservedSectors;
            byte numFats = bpb.NumFats;
            uint rootDirEntries = bpb.RootDirEntries;
            uint sectorsPerFat = bpb.SectorsPerFat16;

            // Calculate cluster count for FAT size
            uint rootDirSectors = ((rootDirEntries * 32) + (bytesPerSector - 1)) / bytesPerSector;
            uint dataStartSector = partitionStart + reservedSectors + (numFats * sectorsPerFat) + rootDirSectors;

            // Initialize FATs
            for (byte fatNum = 0; fatNum < numFats; fatNum++)
            {
                uint fatStartSector = partitionStart + reservedSectors + (fatNum * sectorsPerFat);
                uint fatOffset = fatStartSector * bytesPerSector;

                // FAT16: First entry is media descriptor + high bits of reserved cluster
                imageData[fatOffset] = 0xF8;
                imageData[fatOffset + 1] = 0xFF;
                imageData[fatOffset + 2] = 0xFF;
                imageData[fatOffset + 3] = 0x0F;

                // Second entry (cluster 1) marks end of reserved cluster chain
                imageData[fatOffset + 4] = 0xFF;
                imageData[fatOffset + 5] = 0x0F;
            }

            // Initialize root directory with volume label
            uint rootDirStartSector = partitionStart + reservedSectors + (numFats * sectorsPerFat);
            uint rootDirOffset = rootDirStartSector * bytesPerSector;

            if (!string.IsNullOrEmpty(volumeLabel))
            {
                CreateVolumeLabelEntry(imageData.AsSpan((int)rootDirOffset), volumeLabel);
            }
        }

        private void CreateBlankFloppy(long imageSize, string volumeLabel)
        {
            if (imageSize <= 0 || imageSize % 512 != 0)
                throw new ArgumentException("Image size must be a positive multiple of 512 bytes.");

            if (!string.IsNullOrEmpty(volumeLabel) && volumeLabel.Length > 11)
                volumeLabel = volumeLabel.Substring(0, 11);

            _imageData = new byte[imageSize];
            _partitionStartSector = 0;
            var totalSectors = (uint)(imageSize / 512);

            // Calculate FAT12 parameters
            uint bytesPerSector = 512;
            byte sectorsPerCluster = GetSectorsPerCluster(totalSectors);
            uint reservedSectors = 3; // Standard for FAT12
            byte numFats = 2;
            uint rootDirEntries = 224; // Standard for 360KB+ floppies
            uint sectorsPerFat = CalculateSectorsPerFat(totalSectors, sectorsPerCluster);
            uint sectorsPerTrack = 18; // Standard for high-density floppies
            uint headsPerCylinder = 2; // Standard for high-density floppies

            // Build boot sector
            var bootSector = _imageData.AsSpan(0, 512);

            // Jump instruction to skip BPB
            bootSector[0] = 0xEB;
            bootSector[1] = 0x58; // Jump offset
            bootSector[2] = 0x90; // NOP

            // OEM ID (8 bytes)
            var oemBytes = System.Text.Encoding.ASCII.GetBytes("MSDOS5.0");
            for (int i = 0; i < oemBytes.Length; i++) bootSector[3 + i] = oemBytes[i];

            // Bytes per sector (offset 11, 2 bytes)
            bootSector[11] = (byte)(bytesPerSector & 0xFF);
            bootSector[12] = (byte)((bytesPerSector >> 8) & 0xFF);

            // Sectors per cluster (offset 13, 1 byte)
            bootSector[13] = sectorsPerCluster;

            // Reserved sectors (offset 14, 2 bytes)
            bootSector[14] = (byte)(reservedSectors & 0xFF);
            bootSector[15] = (byte)((reservedSectors >> 8) & 0xFF);

            // Number of FATs (offset 16, 1 byte)
            bootSector[16] = numFats;

            // Root directory entries (offset 17, 2 bytes)
            bootSector[17] = (byte)(rootDirEntries & 0xFF);
            bootSector[18] = (byte)((rootDirEntries >> 8) & 0xFF);

            // Total sectors (16-bit, offset 19, 2 bytes) - set to 0 for >32768 sectors
            bootSector[19] = 0x00;
            bootSector[20] = 0x00;

            // Media descriptor (offset 21, 1 byte) - 0xF8 for 1.44MB floppy
            bootSector[21] = 0xF8;

            // Sectors per FAT (16-bit, offset 22, 2 bytes)
            bootSector[22] = (byte)(sectorsPerFat & 0xFF);
            bootSector[23] = (byte)((sectorsPerFat >> 8) & 0xFF);

            // Sectors per track (offset 24, 2 bytes)
            bootSector[24] = (byte)(sectorsPerTrack & 0xFF);
            bootSector[25] = (byte)((sectorsPerTrack >> 8) & 0xFF);

            // Heads per cylinder (offset 26, 2 bytes)
            bootSector[26] = (byte)(headsPerCylinder & 0xFF);
            bootSector[27] = (byte)((headsPerCylinder >> 8) & 0xFF);

            // Hidden sectors (offset 28, 4 bytes)
            bootSector[28] = 0x00;
            bootSector[29] = 0x00;
            bootSector[30] = 0x00;
            bootSector[31] = 0x00;

            // Total sectors (32-bit, offset 32, 4 bytes)
            bootSector[32] = (byte)(totalSectors & 0xFF);
            bootSector[33] = (byte)((totalSectors >> 8) & 0xFF);
            bootSector[34] = (byte)((totalSectors >> 16) & 0xFF);
            bootSector[35] = (byte)((totalSectors >> 24) & 0xFF);

            // Boot code (offset 63, 448 bytes) - NOPs
            // Already zeroed

            // Boot signature (offset 510, 2 bytes)
            bootSector[510] = 0x55;
            bootSector[511] = 0xAA;

            // Initialize FATs
            InitializeFats(sectorsPerFat, reservedSectors, numFats, bytesPerSector, bootSector[21]);

            // Initialize root directory
            InitializeRootDirectory(rootDirEntries, reservedSectors, numFats, sectorsPerFat, bytesPerSector, volumeLabel);

            _isLoaded = true;
            _fatType = FatType.Fat12;
            VolumeLabel = volumeLabel;
            FilesAndFolders.Clear();
        }

        private byte GetSectorsPerCluster(uint totalSectors)
        {
            // Choose sectors per cluster based on total size
            // Aim for reasonable cluster count for FAT12 (< 4085 clusters)
            if (totalSectors <= 306) // ~160KB
                return 1;
            if (totalSectors <= 720) // ~360KB
                return 1;
            if (totalSectors <= 1440) // ~720KB
                return 1;
            if (totalSectors <= 2880) // ~1.44MB
                return 1;
            return 2; // ~2.88MB
        }

        private uint CalculateSectorsPerFat(uint totalSectors, byte sectorsPerCluster)
        {
            uint reservedSectors = 3;
            byte numFats = 2;
            uint rootDirEntries = 224;
            uint bytesPerSector = 512;

            uint rootDirSectors = ((rootDirEntries * 32) + (bytesPerSector - 1)) / bytesPerSector;
            uint dataStart = reservedSectors + (numFats * 1u) + rootDirSectors; // Start with 1 sector per FAT
            uint dataSectors = totalSectors - dataStart;
            uint clusterCount = dataSectors / sectorsPerCluster;

            // FAT12: 1.5 bytes per cluster entry
            uint fatSectors = ((clusterCount * 3u / 2) + 1) / bytesPerSector;
            if (fatSectors < 1) fatSectors = 1;

            return fatSectors;
        }

        private void InitializeFats(uint sectorsPerFat, uint reservedSectors, byte numFats, uint bytesPerSector, byte mediaDescriptor)
        {
            var imageData = _imageData!;
            for (byte fatNum = 0; fatNum < numFats; fatNum++)
            {
                uint fatStart = reservedSectors + (fatNum * sectorsPerFat);
                uint fatOffset = fatStart * bytesPerSector;

                // FAT12 reserved entries:
                // cluster 0 = media descriptor + 0xF high bits
                // cluster 1 = reserved/end-of-chain marker
                imageData[fatOffset] = mediaDescriptor;
                imageData[fatOffset + 1] = 0xFF;
                imageData[fatOffset + 2] = 0xFF;
            }
        }

        private void InitializeRootDirectory(uint rootDirEntries, uint reservedSectors, byte numFats, uint sectorsPerFat, uint bytesPerSector, string volumeLabel)
        {
            uint rootDirStart = reservedSectors + (numFats * sectorsPerFat);
            uint rootDirOffset = rootDirStart * bytesPerSector;

            // Add volume label if specified
            if (!string.IsNullOrEmpty(volumeLabel))
            {
                CreateVolumeLabelEntry(_imageData.AsSpan((int)rootDirOffset), volumeLabel);
            }
        }

        private void CreateVolumeLabelEntry(Span<byte> entry, string label)
        {
            if (label.Length > 11)
                label = label.Substring(0, 11);

            // Pad label with spaces
            var paddedLabel = label.PadRight(11);
            var labelBytes = System.Text.Encoding.ASCII.GetBytes(paddedLabel);

            // Write name (8 bytes) and extension (3 bytes)
            for (int i = 0; i < 11; i++) entry[i] = labelBytes[i];

            // Attribute: volume label (0x08)
            entry[11] = 0x08;

            // Remaining fields are zeroed (reserved, time, date, clusters, size)
        }

        /// <summary>
        /// Formats the currently loaded disk image by clearing the FAT and root directory.
        /// </summary>
        public void FormatImage()
        {
            if (_imageData == null || !_isLoaded)
                throw new InvalidOperationException("Image must be opened before formatting.");

            var partitionByteOffset = _partitionStartSector * 512;
            var bootSectorSpan = new ReadOnlySpan<byte>(_imageData, (int)partitionByteOffset, 512);
            var bpb = ParseBpb(bootSectorSpan);

            // 1. Clear FAT and Root Directory
            var fatStartOffset = (int)((_partitionStartSector + bpb.ReservedSectors) * bpb.BytesPerSector);
            var fatSizeBytes = (int)(bpb.NumFats * bpb.SectorsPerFat * bpb.BytesPerSector);

            var rootDirStartOffset = fatStartOffset + fatSizeBytes;
            var rootDirSectors = ((bpb.RootDirEntries * 32) + (bpb.BytesPerSector - 1)) / bpb.BytesPerSector;
            var rootDirSizeBytes = (int)(rootDirSectors * bpb.BytesPerSector);

            if (fatStartOffset + fatSizeBytes + rootDirSizeBytes > _imageData.Length)
                throw new InvalidOperationException("Calculated FAT/Root Directory size exceeds image capacity.");

            Array.Clear(_imageData, fatStartOffset, fatSizeBytes + rootDirSizeBytes);

            // 2. Re-initialize FAT Reserved Clusters
            for (byte fatNum = 0; fatNum < bpb.NumFats; fatNum++)
            {
                var currentFatOffset = fatStartOffset + (int)(fatNum * bpb.SectorsPerFat * bpb.BytesPerSector);

                if (IsFat12(bpb))
                {
                    _imageData[currentFatOffset] = bpb.MediaDescriptor;
                    _imageData[currentFatOffset + 1] = 0xFF;
                    _imageData[currentFatOffset + 2] = 0xFF;
                }
                else
                {
                    _imageData[currentFatOffset] = bpb.MediaDescriptor;
                    _imageData[currentFatOffset + 1] = 0xFF;
                    _imageData[currentFatOffset + 2] = 0xFF;
                    _imageData[currentFatOffset + 3] = 0x0F;

                    // FAT16 cluster 1 is end of chain
                    _imageData[currentFatOffset + 4] = 0xFF;
                    _imageData[currentFatOffset + 5] = 0x0F;
                }
            }

            // 3. Re-create Volume Label entry if it existed
            if (!string.IsNullOrEmpty(VolumeLabel))
            {
                CreateVolumeLabelEntry(_imageData.AsSpan(rootDirStartOffset), VolumeLabel);
            }

            // 4. Reload
            ParseFatFilesystem();
        }

        private sealed class OperationProgressTracker(string rootFolderPath, long totalBytes, IProgress<OperationProgress>? progress)
        {
            private readonly string _rootFolderPath = rootFolderPath;
            private readonly long _totalBytes = totalBytes;
            private readonly IProgress<OperationProgress>? _progress = progress;
            private long _processedBytes;

            public void ReportCurrent(string filePath)
            {
                _progress?.Report(new OperationProgress(_processedBytes, _totalBytes, Path.GetRelativePath(_rootFolderPath, filePath)));
            }

            public void Advance(long fileSize, string filePath)
            {
                _processedBytes = Math.Min(_processedBytes + fileSize, _totalBytes);
                _progress?.Report(new OperationProgress(_processedBytes, _totalBytes, Path.GetRelativePath(_rootFolderPath, filePath)));
            }

            public void ReportCompleted()
            {
                _progress?.Report(new OperationProgress(_totalBytes, _totalBytes, string.Empty));
            }
        }

        /// <summary>
        /// Returns a copy of the current image data.
        /// </summary>
        public byte[]? GetImageData()
        {
            if (_imageData == null)
                return null;
            return (byte[])_imageData.Clone();
        }

        /// <summary>
        /// Returns the partition start sector (0 for raw FAT images, >0 for partitioned images).
        /// </summary>
        public uint GetPartitionStartSector()
        {
            return _partitionStartSector;
        }

        /// <summary>
        /// Updates the boot sector at the specified byte offset.
        /// </summary>
        /// <param name="offset">Byte offset where the boot sector starts.</param>
        /// <param name="bootSector">The 512-byte boot sector data.</param>
        public void UpdateBootSector(int offset, byte[] bootSector)
        {
            if (_imageData == null)
                throw new InvalidOperationException("No image data loaded.");
            if (bootSector == null || bootSector.Length != 512)
                throw new ArgumentException("Boot sector must be exactly 512 bytes.");
            if (offset < 0 || offset + 512 > _imageData.Length)
                throw new ArgumentOutOfRangeException(nameof(offset), "Boot sector offset is out of bounds.");

            Array.Copy(bootSector, 0, _imageData, offset, 512);
        }

        /// <summary>
        /// Analyzes the disk image and returns sector map information.
        /// </summary>
        public SectorMapInfo GetSectorMap()
        {
            var info = new SectorMapInfo();

            if (_imageData == null || !_isLoaded)
                return info;

            var bytesPerSector = 512u;
            var totalSectors = (_imageData.Length + 511) / 512;
            info.TotalSectors = totalSectors;
            info.HasPartition = _partitionStartSector > 0;
            info.PartitionStartSector = _partitionStartSector;

            // Initialize all sectors as reserved
            info.SectorTypes = new SectorType[Math.Min(totalSectors, info.MaxSectorsToShow)];
            for (int i = 0; i < info.SectorTypes.Length; i++)
            {
                info.SectorTypes[i] = SectorType.Reserved;
            }

            // Check if we have a valid FAT filesystem
            if (_fatType == null)
            {
                // Mark all as free if no valid filesystem
                for (int i = 0; i < info.SectorTypes.Length; i++)
                {
                    info.SectorTypes[i] = SectorType.Free;
                }
                info.FreeSectors = info.SectorTypes.Length;
                info.TotalFreeSectors = totalSectors;
                return info;
            }

            var bootSectorOffset = _partitionStartSector * 512;
            if (bootSectorOffset + 512 > _imageData.Length)
                return info;

            var bpb = ParseBpb(new ReadOnlySpan<byte>(_imageData, (int)bootSectorOffset, 512));
            info.BytesPerSector = bpb.BytesPerSector;
            info.SectorsPerCluster = bpb.SectorsPerCluster;
            bytesPerSector = bpb.BytesPerSector;

            // Mark MBR sector if present (sector 0)
            if (_partitionStartSector == 0)
            {
                // Raw FAT image - first sector is boot sector
                info.SectorTypes[0] = SectorType.BootSector;
            }
            else
            {
                // Partitioned image - sector 0 contains MBR/partition table
                info.SectorTypes[0] = SectorType.PartitionTable;
            }

            // Calculate filesystem structure
            var partitionSectors = totalSectors - _partitionStartSector;
            var fatStartSector = _partitionStartSector + bpb.ReservedSectors;
            var rootDirStartSector = fatStartSector + (bpb.NumFats * bpb.SectorsPerFat);
            var rootDirSectors = GetRootDirSizeSectors(bpb);
            var dataStartSector = rootDirStartSector + rootDirSectors;
            var dataAreaSectors = Math.Max(0, totalSectors - (int)dataStartSector);
            var totalDataClusters = bpb.SectorsPerCluster == 0 ? 0 : dataAreaSectors / bpb.SectorsPerCluster;
            var usableDataSectors = totalDataClusters * bpb.SectorsPerCluster;
            var dataEndSector = dataStartSector + (uint)usableDataSectors;

            // Helper to check if sector index is within display bounds
            bool IsInDisplayBounds(uint sector) => sector < (uint)info.SectorTypes.Length;

            // Mark reserved sectors (including boot sector within partition)
            for (uint s = _partitionStartSector; s < _partitionStartSector + bpb.ReservedSectors && s < totalSectors; s++)
            {
                if (!IsInDisplayBounds(s)) break;
                if (s == _partitionStartSector)
                {
                    info.SectorTypes[s] = SectorType.BootSector;
                }
                else
                {
                    info.SectorTypes[s] = SectorType.Reserved;
                }
            }

            // Mark FAT sectors
            for (uint fatNum = 0; fatNum < bpb.NumFats; fatNum++)
            {
                for (uint s = fatStartSector + (fatNum * bpb.SectorsPerFat);
                     s < fatStartSector + ((fatNum + 1) * bpb.SectorsPerFat) && s < totalSectors; s++)
                {
                    if (!IsInDisplayBounds(s)) break;
                    info.SectorTypes[s] = SectorType.FatTable;
                }
            }

            // Mark root directory sectors
            for (uint s = rootDirStartSector; s < rootDirStartSector + rootDirSectors && s < totalSectors; s++)
            {
                if (!IsInDisplayBounds(s)) break;
                info.SectorTypes[s] = SectorType.RootDirectory;
            }

            // Calculate allocated data sectors from FAT
            var allocatedClusters = GetAllocatedClusters(bpb);

            foreach (var cluster in allocatedClusters)
            {
                var startSector = dataStartSector + (cluster - 2) * bpb.SectorsPerCluster;
                if (startSector >= dataEndSector)
                {
                    continue;
                }

                info.TotalAllocatedSectors += (int)Math.Min(
                    bpb.SectorsPerCluster,
                    dataEndSector - startSector);

                for (uint i = 0; i < bpb.SectorsPerCluster && startSector + i < dataEndSector; i++)
                {
                    if (!IsInDisplayBounds(startSector + i)) break;
                    info.SectorTypes[startSector + i] = SectorType.Allocated;
                }
            }

            info.TotalFreeSectors = Math.Max(0, usableDataSectors - info.TotalAllocatedSectors);

            // Mark remaining sectors in data area as free (before counting)
            for (uint s = dataStartSector; s < dataEndSector; s++)
            {
                if (!IsInDisplayBounds(s)) break;
                if (info.SectorTypes[s] == SectorType.Reserved)
                {
                    info.SectorTypes[s] = SectorType.Free;
                }
            }

            // Count sector types
            foreach (var type in info.SectorTypes)
            {
                if (type == SectorType.Allocated)
                    info.AllocatedSectors++;
                else if (type == SectorType.Free)
                    info.FreeSectors++;
            }

            return info;
        }

        /// <summary>
        /// Analyzes the disk image and returns cluster map information.
        /// </summary>
        public ClusterMapInfo GetClusterMap()
        {
            var info = new ClusterMapInfo();

            if (_imageData == null || !_isLoaded)
                return info;

            var totalSectors = (_imageData.Length + 511) / 512;
            info.TotalSectors = totalSectors;
            info.HasPartition = _partitionStartSector > 0;
            info.PartitionStartSector = _partitionStartSector;

            // Check if we have a valid FAT filesystem
            if (_fatType == null)
            {
                return info;
            }

            var bootSectorOffset = _partitionStartSector * 512;
            if (bootSectorOffset + 512 > _imageData.Length)
                return info;

            var bpb = ParseBpb(new ReadOnlySpan<byte>(_imageData, (int)bootSectorOffset, 512));
            info.BytesPerSector = bpb.BytesPerSector;
            info.SectorsPerCluster = bpb.SectorsPerCluster;

            // Calculate filesystem structure based on actual image size
            var fatStartSector = _partitionStartSector + bpb.ReservedSectors;
            var rootDirStartSector = fatStartSector + (bpb.NumFats * bpb.SectorsPerFat);
            var rootDirSectors = GetRootDirSizeSectors(bpb);
            var dataStartSector = rootDirStartSector + rootDirSectors;
            var dataSectors = totalSectors - dataStartSector;
            if (dataSectors < 0) dataSectors = 0;

            var totalClusters = (int)(dataSectors / bpb.SectorsPerCluster);
            info.TotalClusters = totalClusters;
            var clustersToShow = Math.Min(totalClusters, info.MaxClustersToShow);

            // Initialize all clusters as free
            info.ClusterTypes = new SectorType[clustersToShow];
            for (int i = 0; i < clustersToShow; i++)
            {
                info.ClusterTypes[i] = SectorType.Free;
            }

            // Get allocated clusters and mark them
            var allocatedClusters = GetAllocatedClusters(bpb);
            foreach (var cluster in allocatedClusters)
            {
                // Cluster numbers start at 2, array index starts at 0
                var index = (int)(cluster - 2);
                if (index < info.ClusterTypes.Length)
                {
                    info.ClusterTypes[index] = SectorType.Allocated;
                    info.AllocatedClusters++;
                }
                info.TotalAllocatedClusters++;
            }

            // Count free clusters (both display and total)
            info.FreeClusters = (int)(clustersToShow - info.AllocatedClusters);
            info.TotalFreeClusters = info.TotalClusters - info.TotalAllocatedClusters;

            return info;
        }

        /// <summary>
        /// Returns a list of allocated cluster numbers from the FAT.
        /// </summary>
        private List<uint> GetAllocatedClusters(Bpb bpb)
        {
            var allocated = new List<uint>();
            var fatStartSector = _partitionStartSector + bpb.ReservedSectors;
            var fatStartOffset = (int)(fatStartSector * bpb.BytesPerSector);
            var fatSize = (int)(bpb.SectorsPerFat * bpb.BytesPerSector);

            // Calculate actual cluster count based on image size
            var totalSectors = (_imageData!.Length + 511) / 512;
            var rootDirStartSector = fatStartSector + (bpb.NumFats * bpb.SectorsPerFat);
            var rootDirSectors = GetRootDirSizeSectors(bpb);
            var dataStartSector = rootDirStartSector + rootDirSectors;
            var dataSectors = totalSectors - dataStartSector;
            if (dataSectors < 0) dataSectors = 0;
            var actualClusterCount = (uint)(dataSectors / bpb.SectorsPerCluster) + 2;

            // Use the larger of BPB cluster count or actual cluster count
            var clusterCount = Math.Max(bpb.ClusterCount + 2, actualClusterCount);

            for (uint cluster = 2; cluster < clusterCount; cluster++)
            {
                uint fatValue;

                if (IsFat12(bpb))
                {
                    var fatEntryStart = fatStartOffset + (int)(cluster * 3 / 2);
                    if (fatEntryStart + 2 > _imageData!.Length)
                        break;

                    var rawValue = _imageData[fatEntryStart] | (_imageData[fatEntryStart + 1] << 8);
                    fatValue = (uint)((cluster & 1) == 0 ? (rawValue & 0xFFF) : (rawValue >> 4));
                }
                else // FAT16
                {
                    var fatEntryStart = fatStartOffset + (int)(cluster * 2);
                    if (fatEntryStart + 2 > _imageData!.Length)
                        break;

                    fatValue = BitConverter.ToUInt16(_imageData.AsSpan(fatEntryStart, 2));
                }

                // Check if this cluster is allocated (not free)
                // A cluster is allocated if its FAT entry is non-zero
                if (fatValue != 0)
                {
                    allocated.Add(cluster);
                }
            }

            return allocated;
        }
    }
}
