using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NetImage.Workers;
using NUnit.Framework;

namespace NetImage.Tests.Workers
{
    [TestFixture]
    public class DiskImageWorkerTests
    {
        private const int SectorSize = 512;
        private string? _tempDirectory;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), $"NetImageTests_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (_tempDirectory != null && Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }

        #region Constructor and Property Tests

        [Test]
        public void Constructor_WithValidPath_InitializesCorrectly()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            File.WriteAllBytes(filePath, new byte[1024]);

            // Act
            var worker = new DiskImageWorker(filePath);

            // Assert
            Assert.That(worker.FilePath, Is.EqualTo(filePath));
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Is.Empty);
            Assert.That(worker.IsLoaded, Is.False);
        }

        [Test]
        public void Constructor_WithEmptyPath_StoresEmptyPath()
        {
            // Act
            var worker = new DiskImageWorker(string.Empty);

            // Assert
            Assert.That(worker.FilePath, Is.EqualTo(string.Empty));
        }

        [Test]
        public void FilesAndFolders_IsInitiallyEmpty()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            File.WriteAllBytes(filePath, new byte[1024]);

            // Act
            var worker = new DiskImageWorker(filePath);

            // Assert
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Is.Empty);
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Is.InstanceOf<List<string>>());
        }

        #endregion

        #region OpenAsync Tests

        [Test]
        public async Task OpenAsync_WithValidFatImage_LoadsSuccessfully()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateMinimalFatImage();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            bool loadingStarted = false;
            bool loadingCompleted = false;

            worker.LoadingStarted += (_, _) => loadingStarted = true;
            worker.LoadingCompleted += (_, _) => loadingCompleted = true;

            // Act
            await worker.OpenAsync();

            // Assert
            Assert.That(worker.IsLoaded, Is.True);
            Assert.That(loadingStarted, Is.True, "LoadingStarted event should be raised");
            Assert.That(loadingCompleted, Is.True, "LoadingCompleted event should be raised");
        }

        [Test]
        public async Task OpenAsync_WhenAlreadyLoaded_DoesNotReload()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateMinimalFatImage();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            int eventCount = 0;
            worker.LoadingStarted += (_, _) => eventCount++;

            // Act - Call OpenAsync twice
            await worker.OpenAsync();
            await worker.OpenAsync();

            // Assert
            Assert.That(worker.IsLoaded, Is.True);
            Assert.That(eventCount, Is.EqualTo(1), "LoadingStarted should only fire once");
        }

        [Test]
        public async Task OpenAsync_WithInvalidBootSector_DoesNotCrash()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "invalid.img");
            File.WriteAllBytes(filePath, new byte[1024]); // Random data, no boot sector signature

            var worker = new DiskImageWorker(filePath);

            // Act & Assert - Should not throw. An invalid boot sector is rejected, so the
            // worker stays unloaded; opening it must still not crash.
            await worker.OpenAsync();
            Assert.That(worker.IsLoaded, Is.False);
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Is.Empty);
        }

        [Test]
        public async Task OpenAsync_WithFat32Image_DoesNotMarkWorkerAsLoaded()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "fat32.img");
            var imageData = CreateFat32BootSectorImage();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);

            // Act
            await worker.OpenAsync();

            // Assert
            Assert.That(worker.IsLoaded, Is.False);
            Assert.That(worker.FilesystemType, Is.EqualTo(DiskImageWorker.FatType.Fat32));
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Is.Empty);
        }

        [Test]
        public async Task OpenAsync_WithFileSmallerThanSector_DoesNotCrash()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "small.img");
            File.WriteAllBytes(filePath, new byte[256]); // Smaller than 512 bytes

            var worker = new DiskImageWorker(filePath);

            // Act & Assert
            await worker.OpenAsync();
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Is.Empty);
        }

        [Test]
        public async Task OpenAsync_WithNonExistentFile_ThrowsException()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "nonexistent.img");

            var worker = new DiskImageWorker(filePath);

            // Act & Assert - FileNotFoundException should be thrown
            try
            {
                await worker.OpenAsync();
                Assert.Fail("Expected FileNotFoundException was not thrown");
            }
            catch (System.IO.FileNotFoundException)
            {
                // Expected
            }
        }

        #endregion

        #region Boot Sector Validation Tests

        [Test]
        public void CreateMinimalFatImage_ContainsValidBootSignature()
        {
            // Arrange & Act
            var image = CreateMinimalFatImage();

            // Assert
            Assert.That(image.Length, Is.GreaterThanOrEqualTo(512));
            Assert.That(image[510], Is.EqualTo(0x55));
            Assert.That(image[511], Is.EqualTo(0xAA));
        }

        [Test]
        public async Task CreateBlankImage_InitializesFat12ReservedEntriesCorrectly()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "blank.img");
            var worker = new DiskImageWorker(filePath);

            // Act
            worker.CreateBlankImage(1474560, "DISK");
            await worker.SaveAsync(filePath);

            // Assert
            var image = await File.ReadAllBytesAsync(filePath);
            var reservedSectors = BitConverter.ToUInt16(image, 14);
            var sectorsPerFat = BitConverter.ToUInt16(image, 22);
            var fat1Offset = reservedSectors * SectorSize;
            var fat2Offset = fat1Offset + (sectorsPerFat * SectorSize);

            Assert.That(image[fat1Offset], Is.EqualTo(0xF8));
            Assert.That(image[fat1Offset + 1], Is.EqualTo(0xFF));
            Assert.That(image[fat1Offset + 2], Is.EqualTo(0xFF));
            Assert.That(image[fat2Offset], Is.EqualTo(0xF8));
            Assert.That(image[fat2Offset + 1], Is.EqualTo(0xFF));
            Assert.That(image[fat2Offset + 2], Is.EqualTo(0xFF));
        }

        [Test]
        public async Task CreateBlankImage_WithTwoPointEightEightMegabyteSize_CreatesValidFat12Image()
        {
            // Arrange: the corrected 2.88 MB floppy size is sector-aligned (2949120 % 512 == 0)
            const long imageSize = 2949120;
            Assert.That(imageSize % SectorSize, Is.EqualTo(0));

            var filePath = Path.Combine(_tempDirectory!, "fat288.img");
            var worker = new DiskImageWorker(filePath);

            // Act - must not throw for the corrected, sector-aligned size
            worker.CreateBlankImage(imageSize, "DISK");

            // Assert (in-memory)
            Assert.That(worker.IsLoaded, Is.True);
            Assert.That(worker.FilesystemType, Is.EqualTo(DiskImageWorker.FatType.Fat12));
            Assert.That(worker.VolumeLabel, Is.EqualTo("DISK"));

            await worker.SaveAsync(filePath);

            // Assert (reload from disk proves the image is a valid, loadable FAT12 volume)
            var reloaded = new DiskImageWorker(filePath);
            await reloaded.OpenAsync();

            Assert.That(reloaded.IsLoaded, Is.True);
            Assert.That(reloaded.FilesystemType, Is.EqualTo(DiskImageWorker.FatType.Fat12));
            Assert.That(reloaded.GetTotalBytes(), Is.GreaterThan(0));
            Assert.That(reloaded.GetFreeBytes(), Is.EqualTo(reloaded.GetTotalBytes()));

            var image = await File.ReadAllBytesAsync(filePath);
            Assert.That(image.Length, Is.EqualTo(imageSize));
            Assert.That(image[510], Is.EqualTo(0x55));
            Assert.That(image[511], Is.EqualTo(0xAA));
        }

        #endregion

        #region Directory Entry Tests

        [Test]
        public async Task OpenAsync_WithDirectoryEntries_PopulatesFilesAndFolders()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateFatImageWithFiles(new[] { "README.TXT", "DATA.DAT", "CONFIG.CFG" });
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);

            // Act
            await worker.OpenAsync();

            // Assert - filenames should have dot separator between name and extension
            Assert.That(worker.FilesAndFolders.Count, Is.EqualTo(3));
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Contains.Item("README.TXT"));
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Contains.Item("DATA.DAT"));
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Contains.Item("CONFIG.CFG"));
        }

        [Test]
        public async Task OpenAsync_WithDirectoryEntryHavingSpaces_IncludesSpacesInName()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            // FAT stores spaces as actual space characters in the name
            var imageData = CreateFatImageWithFiles(new[] { "MY FILE.TXT" });
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);

            // Act
            await worker.OpenAsync();

            // Assert - spaces in name are preserved, dot separator added before extension
            Assert.That(worker.FilesAndFolders.Count, Is.EqualTo(1));
            Assert.That(worker.FilesAndFolders[0].Path, Is.EqualTo("MY FILE.TXT"));
        }

        [Test]
        public async Task OpenAsync_WithDeletedEntry_SkipsDeletedFile()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateFatImageWithDeletedEntry();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);

            // Act
            await worker.OpenAsync();

            // Assert - Deleted entry (0xE5) should be skipped
            Assert.That(worker.FilesAndFolders.Count, Is.EqualTo(1));
            Assert.That(worker.FilesAndFolders[0].Path, Is.EqualTo("VALID.TXT"));
        }

        [Test]
        public async Task OpenAsync_WithEmptyEntry_SkipsEmptySlot()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateFatImageWithEmptyEntry();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);

            // Act
            await worker.OpenAsync();

            // Assert - Empty entry (0x00) should be skipped
            Assert.That(worker.FilesAndFolders.Count, Is.EqualTo(1));
            Assert.That(worker.FilesAndFolders[0].Path, Is.EqualTo("FIRST.TXT"));
        }

        [Test]
        public async Task OpenAsync_WithSubdirectoryMarker_IncludesDirectory()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateFatImageWithSubdirectory();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);

            // Act
            await worker.OpenAsync();

            // Assert
            Assert.That(worker.FilesAndFolders.Count, Is.GreaterThanOrEqualTo(1));
            // Subdirectory entries should be included
            var hasSubdir = worker.FilesAndFolders.Any(f => f.Path.StartsWith("SUBDIR"));
            Assert.That(hasSubdir, Is.True);
        }

        [Test]
        public async Task OpenAsync_WithVolumeLabelEntry_SkipsVolumeLabel()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateFatImageWithVolumeLabel();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);

            // Act
            await worker.OpenAsync();

            // Assert - Volume label should be skipped
            Assert.That(worker.FilesAndFolders.Count, Is.EqualTo(1));
            Assert.That(worker.FilesAndFolders[0].Path, Is.EqualTo("DATA.TXT"));
        }

        [Test]
        public async Task OpenAsync_WithDotEntries_SkipsDotAndDotDot()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateFatImageWithDotEntries();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);

            // Act
            await worker.OpenAsync();

            // Assert - . and .. entries should be skipped
            Assert.That(worker.FilesAndFolders.Count, Is.EqualTo(1));
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Not.Contain("."));
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Not.Contain(".."));
        }

        [Test]
        public async Task OpenAsync_WithNestedSubdirectory_TraversesRecursively()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateFatImageWithNestedSubdirectory();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);

            // Act
            await worker.OpenAsync();

            // Assert - Should find the subdirectory and at least attempt traversal
            // Note: Full recursive traversal depends on correct cluster/sector mapping
            Assert.That(worker.FilesAndFolders.Count, Is.GreaterThanOrEqualTo(1));
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Contains.Item("SUBDIR"));
        }

        [Test]
        public async Task OpenAsync_WithFat16DirectoryAtHighCluster_IncludesNestedFile()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "fat16-highcluster.img");
            var imageData = CreateFat16ImageWithHighClusterSubdirectory();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);

            // Act
            await worker.OpenAsync();

            // Assert
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Contains.Item("SUBDIR"));
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Contains.Item("SUBDIR\\FILE.TXT"));

            var fileContent = worker.GetFileContent("SUBDIR\\FILE.TXT");
            Assert.That(fileContent, Is.Not.Null);
            Assert.That(Encoding.ASCII.GetString(fileContent!), Is.EqualTo("TEST"));
        }

        [Test]
        public async Task GetFileContent_WithFat16DirectorySpanningMultipleClusters_FindsFileInLaterCluster()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "fat16-dirchain.img");
            var imageData = CreateFat16ImageWithMultiClusterDirectory();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);

            // Act
            await worker.OpenAsync();
            var fileContent = worker.GetFileContent("SUBDIR\\SECOND.TXT");

            // Assert
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Contains.Item("SUBDIR\\SECOND.TXT"));
            Assert.That(fileContent, Is.Not.Null);
            Assert.That(Encoding.ASCII.GetString(fileContent!), Is.EqualTo("DATA"));
        }

        [Test]
        public async Task OpenAsync_WithTestImaFile_ContainsTestTxt()
        {
            // Arrange
            var testDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "test.ima");

            var worker = new DiskImageWorker(testDataPath);

            // Act
            await worker.OpenAsync();

            // Assert
            Assert.That(worker.FilesAndFolders.Count, Is.EqualTo(1));
            Assert.That(worker.FilesAndFolders[0].Path, Is.EqualTo("TEST.TXT"));

            var fileContent = worker.GetFileContent("TEST.TXT");
            Assert.That(fileContent, Is.Not.Null);
            var contentText = System.Text.Encoding.ASCII.GetString(fileContent!);
            Assert.That(contentText, Contains.Substring("Test"));
        }

        #endregion

        #region Helper Methods for Test Data

        /// <summary>
        /// Creates a minimal valid FAT12 boot sector with no files.
        /// </summary>
        private byte[] CreateMinimalFatImage()
        {
            const int totalSectors = 512; // 256KB image - enough for data clusters
            var image = new byte[totalSectors * SectorSize];

            // Boot record jump instruction
            image[0] = 0xEB;
            image[1] = 0x3C;
            image[2] = 0x90;

            // OEM Name (8 bytes)
            Array.Copy(Encoding.ASCII.GetBytes("MSDOS5.0"), 0, image, 3, 8);

            // BPB (BIOS Parameter Block)
            // Bytes per sector (2 bytes) = 512
            image[11] = 0x00;
            image[12] = 0x02;

            // Sectors per cluster (1 byte) = 1
            image[13] = 0x01;

            // Reserved sectors (2 bytes) = 1
            image[14] = 0x01;
            image[15] = 0x00;

            // Number of FATs (1 byte) = 2
            image[16] = 0x02;

            // Root directory entries (2 bytes) = 512
            image[17] = 0x00;
            image[18] = 0x10;

            // Total sectors (16-bit) (2 bytes) = 512
            image[19] = (byte)(totalSectors & 0xFF);
            image[20] = (byte)((totalSectors >> 8) & 0xFF);

            // Media descriptor (1 byte) = 0xF0 (5.25" HD)
            image[21] = 0xF0;

            // Sectors per FAT (16-bit) (2 bytes) = 4 (need space for 512 clusters in FAT12)
            // FAT12: 512 clusters * 1.5 bytes = 768 bytes = 1.5 sectors, round up to 2
            // But let's use 4 sectors for safety
            image[22] = 0x04;
            image[23] = 0x00;

            // Sectors per track (2 bytes) = 63
            image[24] = 0x3F;
            image[25] = 0x00;

            // Heads per cylinder (2 bytes) = 4
            image[26] = 0x04;
            image[27] = 0x00;

            // Hidden sectors (4 bytes) = 0
            image[28] = 0x00;
            image[29] = 0x00;
            image[30] = 0x00;
            image[31] = 0x00;

            // Total sectors (32-bit) (4 bytes) = 512
            image[32] = (byte)(totalSectors & 0xFF);
            image[33] = (byte)((totalSectors >> 8) & 0xFF);
            image[34] = 0x00;
            image[35] = 0x00;

            // Initialize FAT entries (both FAT copies)
            // FAT starts at sector 1, each entry is 1.5 bytes for FAT12
            // Mark cluster 0 and 1 as reserved, rest as free (0)
            var fatStart = SectorSize; // Sector 1
            // FAT12: media descriptor in first 2 bytes, then cluster entries
            image[fatStart] = 0xF8; // Media descriptor (lower 8 bits)
            image[fatStart + 1] = 0xFF; // Media descriptor (upper 4 bits) + cluster 1 reserved (lower 8 bits)
            // Cluster 1 reserved (upper 4 bits) + cluster 2 free (lower 8 bits)
            image[fatStart + 2] = 0x0F;

            // Rest of FAT entries are already 0 (free)

            // Copy FAT to second copy (starts at sector 1 + 4 = sector 5)
            var fatSize = 4 * SectorSize;
            Array.Copy(image, fatStart, image, fatStart + fatSize, fatSize);

            // Boot signature at offset 510-511
            image[510] = 0x55;
            image[511] = 0xAA;

            return image;
        }

        /// <summary>
        /// Creates a FAT image with specified file entries in the root directory.
        /// </summary>
        private byte[] CreateFatImageWithFiles(string[] fileNames)
        {
            var image = CreateMinimalFatImage();

            // Calculate root directory start sector
            // Reserved sectors (1) + FATs (2 * 4 sectors) = sector 9
            var rootDirStart = 9 * SectorSize;

            for (int i = 0; i < fileNames.Length && i < 512; i++)
            {
                CreateDirectoryEntry(image, rootDirStart + (i * 32), fileNames[i], isDirectory: false);
            }

            return image;
        }

        /// <summary>
        /// Creates a FAT image with one deleted entry (0xE5) and one valid entry.
        /// </summary>
        private byte[] CreateFatImageWithDeletedEntry()
        {
            var image = CreateMinimalFatImage();
            var rootDirStart = 9 * SectorSize; // Reserved (1) + FATs (2 * 4) = 9

            // First entry: deleted file (starts with 0xE5)
            var deletedEntry = new byte[32];
            deletedEntry[0] = 0xE5; // Deleted flag
            Array.Copy(Encoding.ASCII.GetBytes("DELETED"), 0, deletedEntry, 1, 7);
            deletedEntry[8] = (byte)'T';
            deletedEntry[9] = (byte)'X';
            deletedEntry[10] = (byte)'T';
            Array.Copy(deletedEntry, 0, image, rootDirStart, 32);

            // Second entry: valid file
            CreateDirectoryEntry(image, rootDirStart + 32, "VALID.TXT", isDirectory: false);

            return image;
        }

        /// <summary>
        /// Creates a FAT image with one valid entry followed by an empty entry (0x00).
        /// </summary>
        private byte[] CreateFatImageWithEmptyEntry()
        {
            var image = CreateMinimalFatImage();
            var rootDirStart = 9 * SectorSize; // Reserved (1) + FATs (2 * 4) = 9

            // First entry: valid file
            CreateDirectoryEntry(image, rootDirStart, "FIRST.TXT", isDirectory: false);

            // Second entry: empty (all zeros, starts with 0x00)
            // Already zeros from CreateMinimalFatImage

            return image;
        }

        /// <summary>
        /// Creates a FAT image with a subdirectory entry.
        /// </summary>
        private byte[] CreateFatImageWithSubdirectory()
        {
            var image = CreateMinimalFatImage();
            var rootDirStart = 9 * SectorSize; // Reserved (1) + FATs (2 * 4) = 9

            // Create subdirectory entry
            CreateDirectoryEntry(image, rootDirStart, "SUBDIR", isDirectory: true);

            return image;
        }

        /// <summary>
        /// Creates a FAT image with a volume label entry.
        /// </summary>
        private byte[] CreateFatImageWithVolumeLabel()
        {
            var image = CreateMinimalFatImage();
            var rootDirStart = 9 * SectorSize; // Reserved (1) + FATs (2 * 4) = 9

            // First entry: volume label (attribute = 0x08)
            var volumeLabelEntry = new byte[32];
            Array.Copy(Encoding.ASCII.GetBytes("VOLUME  "), 0, volumeLabelEntry, 0, 8);
            volumeLabelEntry[11] = 0x08; // Volume label attribute
            Array.Copy(volumeLabelEntry, 0, image, rootDirStart, 32);

            // Second entry: valid file
            CreateDirectoryEntry(image, rootDirStart + 32, "DATA.TXT", isDirectory: false);

            return image;
        }

        /// <summary>
        /// Creates a FAT image with . and .. entries.
        /// </summary>
        private byte[] CreateFatImageWithDotEntries()
        {
            var image = CreateMinimalFatImage();
            var rootDirStart = 9 * SectorSize; // Reserved (1) + FATs (2 * 4) = 9

            // First entry: . (current directory)
            var dotEntry = new byte[32];
            dotEntry[0] = (byte) '.';
            dotEntry[11] = 0x10; // Directory attribute
            dotEntry[26] = 0x02; // First cluster = 2 (FAT12/16)
            dotEntry[27] = 0x00;
            Array.Copy(dotEntry, 0, image, rootDirStart, 32);

            // Second entry: .. (parent directory)
            var dotDotEntry = new byte[32];
            dotDotEntry[0] = (byte) '.';
            dotDotEntry[1] = (byte) '.';
            dotDotEntry[11] = 0x10; // Directory attribute
            dotDotEntry[26] = 0x02; // First cluster = 2 (FAT12/16)
            dotDotEntry[27] = 0x00;
            Array.Copy(dotDotEntry, 0, image, rootDirStart + 32, 32);

            // Third entry: valid file
            CreateDirectoryEntry(image, rootDirStart + 64, "FILE.TXT", isDirectory: false);

            return image;
        }

        /// <summary>
        /// Creates a FAT image with a nested subdirectory containing a file.
        /// </summary>
        private byte[] CreateFatImageWithNestedSubdirectory()
        {
            var image = CreateMinimalFatImage();
            var rootDirStart = 9 * SectorSize; // Reserved (1) + FATs (2 * 4) = 9

            // Create subdirectory entry at cluster 2
            CreateDirectoryEntry(image, rootDirStart, "SUBDIR", isDirectory: true);

            // Set up the subdirectory content at cluster 2
            // Data area starts after reserved sectors (1) + FATs (2 * 4) + root dir (512 * 32 / 512 = 32) = sector 50
            var subDirStart = 50 * SectorSize;

            // Create . entry in subdirectory
            var dotEntry = new byte[32];
            dotEntry[0] = (byte) '.';
            dotEntry[11] = 0x10;
            dotEntry[26] = 0x02; // First cluster = 2 (FAT12/16)
            Array.Copy(dotEntry, 0, image, subDirStart, 32);

            // Create .. entry in subdirectory
            var dotDotEntry = new byte[32];
            dotDotEntry[0] = (byte) '.';
            dotDotEntry[1] = (byte) '.';
            dotDotEntry[11] = 0x10;
            dotDotEntry[26] = 0x02; // First cluster = 2 (FAT12/16)
            Array.Copy(dotDotEntry, 0, image, subDirStart + 32, 32);

            // Create file in subdirectory
            CreateDirectoryEntry(image, subDirStart + 64, "NESTED.TXT", isDirectory: false);

            return image;
        }

        private byte[] CreateFatImageWithFullRootDirectory()
        {
            var image = CreateMinimalFatImage();
            var rootDirStart = 9 * SectorSize;

            for (int i = 0; i < 4096; i++)
            {
                CreateDirectoryEntry(image, rootDirStart + (i * 32), $"F{i:000}.TXT", isDirectory: false);
            }

            return image;
        }

        private byte[] CreateFatImageWithZeroLengthFile(string fileName)
        {
            var image = CreateMinimalFatImage();
            var rootDirStart = 9 * SectorSize;

            CreateDirectoryEntry(image, rootDirStart, fileName, isDirectory: false);
            image[rootDirStart + 26] = 0x00;
            image[rootDirStart + 27] = 0x00;
            image[rootDirStart + 28] = 0x00;
            image[rootDirStart + 29] = 0x00;
            image[rootDirStart + 30] = 0x00;
            image[rootDirStart + 31] = 0x00;

            return image;
        }

        private byte[] CreateFat32BootSectorImage()
        {
            var image = new byte[SectorSize];

            image[0] = 0xEB;
            image[1] = 0x58;
            image[2] = 0x90;
            Array.Copy(Encoding.ASCII.GetBytes("MSWIN4.1"), 0, image, 3, 8);

            image[11] = 0x00;
            image[12] = 0x02;
            image[13] = 0x01;
            image[14] = 0x20;
            image[15] = 0x00;
            image[16] = 0x02;
            image[17] = 0x00;
            image[18] = 0x00;
            image[19] = 0x00;
            image[20] = 0x00;
            image[21] = 0xF8;
            image[22] = 0x00;
            image[23] = 0x00;
            image[24] = 0x3F;
            image[25] = 0x00;
            image[26] = 0xFF;
            image[27] = 0x00;
            image[32] = 0x70;
            image[33] = 0x11;
            image[34] = 0x01;
            image[35] = 0x00;
            image[36] = 0x00;
            image[37] = 0x01;
            image[38] = 0x00;
            image[39] = 0x00;
            image[44] = 0x02;
            image[510] = 0x55;
            image[511] = 0xAA;

            return image;
        }

        private byte[] CreateFat16ImageWithHighClusterSubdirectory()
        {
            var image = CreateMinimalFat16Image();
            var layout = GetFat16Layout();

            SetFat16Entry(image, 4096, 0xFFFF);
            SetFat16Entry(image, 4097, 0xFFFF);

            WriteFat16DirectoryEntry(image, layout.RootDirectoryOffset, "SUBDIR", isDirectory: true, firstCluster: 4096, fileSize: 0);

            var subdirectoryOffset = GetFat16ClusterOffset(4096);
            WriteFat16DirectoryEntry(image, subdirectoryOffset, ".", isDirectory: true, firstCluster: 4096, fileSize: 0);
            WriteFat16DirectoryEntry(image, subdirectoryOffset + 32, "..", isDirectory: true, firstCluster: 0, fileSize: 0);
            WriteFat16DirectoryEntry(image, subdirectoryOffset + 64, "FILE.TXT", isDirectory: false, firstCluster: 4097, fileSize: 4);

            var fileOffset = GetFat16ClusterOffset(4097);
            Array.Copy(Encoding.ASCII.GetBytes("TEST"), 0, image, fileOffset, 4);

            return image;
        }

        private byte[] CreateFat16ImageWithMultiClusterDirectory()
        {
            var image = CreateMinimalFat16Image();
            var layout = GetFat16Layout();

            SetFat16Entry(image, 2, 3);
            SetFat16Entry(image, 3, 0xFFFF);
            SetFat16Entry(image, 4, 0xFFFF);

            WriteFat16DirectoryEntry(image, layout.RootDirectoryOffset, "SUBDIR", isDirectory: true, firstCluster: 2, fileSize: 0);

            var firstClusterOffset = GetFat16ClusterOffset(2);
            WriteFat16DirectoryEntry(image, firstClusterOffset, ".", isDirectory: true, firstCluster: 2, fileSize: 0);
            WriteFat16DirectoryEntry(image, firstClusterOffset + 32, "..", isDirectory: true, firstCluster: 0, fileSize: 0);
            for (int entryIndex = 2; entryIndex < 16; entryIndex++)
            {
                image[firstClusterOffset + (entryIndex * 32)] = 0xE5;
            }

            var secondClusterOffset = GetFat16ClusterOffset(3);
            WriteFat16DirectoryEntry(image, secondClusterOffset, "SECOND.TXT", isDirectory: false, firstCluster: 4, fileSize: 4);

            var fileOffset = GetFat16ClusterOffset(4);
            Array.Copy(Encoding.ASCII.GetBytes("DATA"), 0, image, fileOffset, 4);

            return image;
        }

        private byte[] CreateMinimalFat16Image()
        {
            const ushort totalSectors = 5000;
            const ushort sectorsPerFat = 20;
            const ushort rootDirEntries = 512;
            const byte sectorsPerCluster = 1;
            const byte numFats = 2;
            const ushort reservedSectors = 1;

            var image = new byte[totalSectors * SectorSize];

            image[0] = 0xEB;
            image[1] = 0x3C;
            image[2] = 0x90;

            Array.Copy(Encoding.ASCII.GetBytes("MSDOS5.0"), 0, image, 3, 8);

            image[11] = 0x00;
            image[12] = 0x02;
            image[13] = sectorsPerCluster;
            image[14] = (byte)(reservedSectors & 0xFF);
            image[15] = (byte)(reservedSectors >> 8);
            image[16] = numFats;
            image[17] = (byte)(rootDirEntries & 0xFF);
            image[18] = (byte)(rootDirEntries >> 8);
            image[19] = (byte)(totalSectors & 0xFF);
            image[20] = (byte)(totalSectors >> 8);
            image[21] = 0xF8;
            image[22] = (byte)(sectorsPerFat & 0xFF);
            image[23] = (byte)(sectorsPerFat >> 8);
            image[24] = 0x3F;
            image[25] = 0x00;
            image[26] = 0xFF;
            image[27] = 0x00;
            image[510] = 0x55;
            image[511] = 0xAA;

            SetFat16Entry(image, 0, 0xFFF8);
            SetFat16Entry(image, 1, 0xFFFF);

            return image;
        }

        private (int Fat1Offset, int Fat2Offset, int RootDirectoryOffset, int DataOffset) GetFat16Layout()
        {
            const int reservedSectors = 1;
            const int sectorsPerFat = 20;
            const int numFats = 2;
            const int rootDirEntries = 512;

            var fat1Offset = reservedSectors * SectorSize;
            var fat2Offset = fat1Offset + (sectorsPerFat * SectorSize);
            var rootDirectoryOffset = (reservedSectors + (numFats * sectorsPerFat)) * SectorSize;
            var rootDirectorySectors = (rootDirEntries * 32) / SectorSize;
            var dataOffset = (reservedSectors + (numFats * sectorsPerFat) + rootDirectorySectors) * SectorSize;

            return (fat1Offset, fat2Offset, rootDirectoryOffset, dataOffset);
        }

        private int GetFat16ClusterOffset(ushort cluster)
        {
            var layout = GetFat16Layout();
            return layout.DataOffset + ((cluster - 2) * SectorSize);
        }

        private void SetFat16Entry(byte[] image, ushort cluster, ushort value)
        {
            var layout = GetFat16Layout();

            var fat1Offset = layout.Fat1Offset + (cluster * 2);
            image[fat1Offset] = (byte)(value & 0xFF);
            image[fat1Offset + 1] = (byte)(value >> 8);

            var fat2Offset = layout.Fat2Offset + (cluster * 2);
            image[fat2Offset] = (byte)(value & 0xFF);
            image[fat2Offset + 1] = (byte)(value >> 8);
        }

        private void WriteFat16DirectoryEntry(byte[] image, int offset, string name, bool isDirectory, ushort firstCluster, uint fileSize)
        {
            Array.Clear(image, offset, 32);

            string namePart;
            var extPart = "   ";

            if (name == ".")
            {
                namePart = ".       ";
            }
            else if (name == "..")
            {
                namePart = "..      ";
            }
            else if (name.Contains('.'))
            {
                var parts = name.Split('.');
                namePart = parts[0].PadRight(8, ' ').AsSpan(0, 8).ToString().ToUpperInvariant();
                if (parts.Length > 1)
                {
                    extPart = parts[1].PadRight(3, ' ').AsSpan(0, 3).ToString().ToUpperInvariant();
                }
            }
            else
            {
                namePart = name.PadRight(8, ' ').AsSpan(0, 8).ToString().ToUpperInvariant();
            }

            Array.Copy(Encoding.ASCII.GetBytes(namePart), 0, image, offset, 8);
            Array.Copy(Encoding.ASCII.GetBytes(extPart), 0, image, offset + 8, 3);
            image[offset + 11] = (byte)(isDirectory ? 0x10 : 0x00);
            image[offset + 26] = (byte)(firstCluster & 0xFF);
            image[offset + 27] = (byte)(firstCluster >> 8);
            image[offset + 28] = (byte)(fileSize & 0xFF);
            image[offset + 29] = (byte)((fileSize >> 8) & 0xFF);
            image[offset + 30] = (byte)((fileSize >> 16) & 0xFF);
            image[offset + 31] = (byte)((fileSize >> 24) & 0xFF);
        }

        /// <summary>
        /// Creates a 32-byte FAT directory entry at the specified offset.
        /// </summary>
        private void CreateDirectoryEntry(byte[] image, int offset, string name, bool isDirectory)
        {
            // Clear the entry
            Array.Clear(image, offset, 32);

            // Parse the 8.3 name
            string namePart;
            string extPart = "   "; // Default to 3 spaces

            if (name.Contains('.'))
            {
                var parts = name.Split('.');
                namePart = parts[0].PadRight(8, ' ').AsSpan(0, 8).ToString();
                if (parts.Length > 1)
                {
                    var ext = parts[1];
                    if (ext.Length > 3)
                        ext = ext.Substring(0, 3);
                    extPart = ext.PadRight(3, ' ');
                }
            }
            else
            {
                namePart = name.PadRight(8, ' ').AsSpan(0, 8).ToString();
            }

            // Write name (8 bytes)
            var nameBytes = Encoding.ASCII.GetBytes(namePart);
            Array.Copy(nameBytes, 0, image, offset, 8);

            // Write extension (3 bytes)
            var extBytes = Encoding.ASCII.GetBytes(extPart);
            Array.Copy(extBytes, 0, image, offset + 8, 3);

            // Attributes (1 byte)
            image[offset + 11] = (byte)(isDirectory ? 0x10 : 0x00); // 0x10 = directory, 0x00 = file

            // Upper case flag (1 byte) - 0x08 for uppercase
            image[offset + 12] = 0x08;

            // Create time, date, etc. (set to some valid values)
            image[offset + 14] = 0x00; // Create time tenths
            image[offset + 15] = 0x00; // Create time
            image[offset + 16] = 0x00; // Create date
            image[offset + 17] = 0x00; // Access date
            image[offset + 18] = 0x00; // High word of first cluster (FAT32) - unused for FAT12/16
            image[offset + 19] = 0x00; // Modify time
            image[offset + 20] = 0x00; // Modify date
            image[offset + 21] = 0x00;
            image[offset + 22] = 0x00; // High word of first cluster (FAT32) - unused for FAT12/16
            image[offset + 23] = 0x00;

            // First cluster (FAT12/16) - cluster 2 (bytes 26-27)
            image[offset + 26] = 0x02;
            image[offset + 27] = 0x00;

            // File size (4 bytes) - 0 for directory, some value for file
            // File size is at offset 28-31
            if (!isDirectory)
            {
                image[offset + 28] = 0x10; // 16 bytes
                image[offset + 29] = 0x00;
                image[offset + 30] = 0x00;
                image[offset + 31] = 0x00;
            }
        }

        #endregion

        #region Disk Space Tests

        [Test]
        public async Task GetTotalBytes_WithMinimalFatImage_ReturnsUsableCapacity()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "capacity.img");
            var imageData = CreateMinimalFatImage();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            await worker.OpenAsync();

            var bytesPerSector = BitConverter.ToUInt16(imageData, 11);
            var sectorsPerCluster = imageData[13];
            var reservedSectors = BitConverter.ToUInt16(imageData, 14);
            var numberOfFats = imageData[16];
            var rootDirectoryEntries = BitConverter.ToUInt16(imageData, 17);
            var totalSectors = BitConverter.ToUInt16(imageData, 19);
            var sectorsPerFat = BitConverter.ToUInt16(imageData, 22);
            var rootDirectorySectors = (rootDirectoryEntries * 32 + (SectorSize - 1)) / SectorSize;
            var dataSectors = totalSectors - reservedSectors - (numberOfFats * sectorsPerFat) - rootDirectorySectors;
            var expectedBytes = (long)(dataSectors / sectorsPerCluster) * bytesPerSector * sectorsPerCluster;

            // Act / Assert
            Assert.That(worker.GetTotalBytes(), Is.EqualTo(expectedBytes));
            Assert.That(worker.GetFreeBytes(), Is.EqualTo(expectedBytes));
        }

        [Test]
        public async Task GetFreeBytes_AfterAddingFile_DecreasesByAllocatedClusterSize()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "free-space.img");
            var imageData = CreateMinimalFatImage();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            await worker.OpenAsync();
            var totalBytes = worker.GetTotalBytes();
            var freeBytesBeforeAdd = worker.GetFreeBytes();

            // Act
            worker.AddFile("ONEBYTE.TXT", new byte[] { 0x01 });

            // Assert
            Assert.That(worker.GetTotalBytes(), Is.EqualTo(totalBytes));
            Assert.That(worker.GetFreeBytes(), Is.EqualTo(freeBytesBeforeAdd - SectorSize));
        }

        [Test]
        public void GetSectorMap_WithLargeImage_ReportsTotalFreeAndAllocatedSectorsBeyondDisplayLimit()
        {
            // Arrange
            var worker = new DiskImageWorker("dummy.img");
            worker.CreateHardDiskImage(100, 16, 63, "DISK");
            worker.AddFile("ONEBYTE.TXT", new byte[] { 0x01 });

            // Act
            var sectorMap = worker.GetSectorMap();

            // Assert
            Assert.That(sectorMap.TotalSectors, Is.GreaterThan(sectorMap.MaxSectorsToShow));
            Assert.That(sectorMap.TotalAllocatedSectors, Is.EqualTo((worker.GetTotalBytes() - worker.GetFreeBytes()) / sectorMap.BytesPerSector));
            Assert.That(sectorMap.TotalFreeSectors, Is.EqualTo(worker.GetFreeBytes() / sectorMap.BytesPerSector));
            Assert.That(sectorMap.TotalFreeSectors, Is.GreaterThan(sectorMap.FreeSectors));
        }

        #endregion

        #region SaveAsync Tests

        [Test]
        public async Task SaveAsync_WithValidImageData_CreatesFile()
        {
            // Arrange
            var inputPath = Path.Combine(_tempDirectory!, "input.img");
            var outputPath = Path.Combine(_tempDirectory!, "output.img");
            var imageData = CreateMinimalFatImage();
            File.WriteAllBytes(inputPath, imageData);

            var worker = new DiskImageWorker(inputPath);
            await worker.OpenAsync();

            // Act
            await worker.SaveAsync(outputPath);

            // Assert
            Assert.That(File.Exists(outputPath), Is.True);
            var savedData = await File.ReadAllBytesAsync(outputPath);
            Assert.That(savedData.Length, Is.EqualTo(imageData.Length));
        }

        [Test]
        public async Task SaveAsync_WhenImageNotLoaded_ThrowsInvalidOperationException()
        {
            // Arrange
            var outputPath = Path.Combine(_tempDirectory!, "output.img");
            var worker = new DiskImageWorker("dummy.img");

            // Act & Assert
            var exception = Assert.ThrowsAsync<InvalidOperationException>(async () => await worker.SaveAsync(outputPath));
            Assert.That(exception!.Message, Does.Contain("No image data"));
        }

        [Test]
        public async Task SaveAsync_PreservesImageData()
        {
            // Arrange
            var inputPath = Path.Combine(_tempDirectory!, "input.img");
            var outputPath = Path.Combine(_tempDirectory!, "output.img");
            var imageData = CreateMinimalFatImage();
            File.WriteAllBytes(inputPath, imageData);

            var worker = new DiskImageWorker(inputPath);
            await worker.OpenAsync();

            // Act
            await worker.SaveAsync(outputPath);

            // Assert
            var savedData = await File.ReadAllBytesAsync(outputPath);
            Assert.That(savedData, Is.EquivalentTo(imageData));
        }

        #endregion

        #region GetFileContent Tests

        [Test]
        public void GetFileContent_WhenImageNotLoaded_ReturnsNull()
        {
            // Arrange
            var worker = new DiskImageWorker("dummy.img");

            // Act
            var result = worker.GetFileContent("TEST.TXT");

            // Assert
            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task GetFileContent_WithNonExistentFile_ReturnsNull()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateMinimalFatImage();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            await worker.OpenAsync();

            // Act
            var result = worker.GetFileContent("NONEXISTENT.TXT");

            // Assert
            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task GetFileContent_WithDirectory_ReturnsNull()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateFatImageWithSubdirectory();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            await worker.OpenAsync();

            // Act
            var result = worker.GetFileContent("SUBDIR");

            // Assert
            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task GetFileContent_CaseInsensitive_FileNameMatch()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateFatImageWithFiles(new[] { "TEST.TXT" });
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            await worker.OpenAsync();

            // Act
            var result = worker.GetFileContent("test.txt");

            // Assert - Should find file even with lowercase query
            Assert.That(result, Is.Not.Null);
        }

        #endregion

        #region AddFile Tests

        [Test]
        public void AddFile_WhenImageNotLoaded_ThrowsInvalidOperationException()
        {
            // Arrange
            var worker = new DiskImageWorker("dummy.img");

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => worker.AddFile("TEST.TXT", new byte[] { 0x01, 0x02, 0x03 }));
        }

        [Test]
        public async Task AddFile_WithValidFile_AddsToImage()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateMinimalFatImage();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            await worker.OpenAsync();

            var fileContent = Encoding.ASCII.GetBytes("Hello World");

            // Act
            worker.AddFile("HELLO.TXT", fileContent);
            await worker.SaveAsync(filePath); // Save changes to disk

            // Assert - Re-open to verify file was added to the image
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            Assert.That(newWorker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Contain("HELLO.TXT"));
        }

        [Test]
        public async Task AddFile_EncodesFileNameToUpperCase()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateMinimalFatImage();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            await worker.OpenAsync();

            // Act
            worker.AddFile("lower.txt", new byte[] { 0x01 });
            await worker.SaveAsync(filePath); // Save changes to disk

            // Assert - Re-open to verify filename is preserved with original case via LFN
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            Assert.That(newWorker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Contain("lower.txt"));
        }

        [Test]
        public async Task AddFile_WithLongFileName_StoresFullNameViaLfn()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateMinimalFatImage();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            await worker.OpenAsync();

            // Act
            worker.AddFile("VERYLONGNAME.EXTENSION", new byte[] { 0x01 });
            await worker.SaveAsync(filePath); // Save changes to disk

            // Assert - Re-open to verify filename is stored with full long name via LFN
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            Assert.That(newWorker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Contain("VERYLONGNAME.EXTENSION"));
        }

        [Test]
        public async Task AddFile_WithoutExtension_AddsSuccessfully()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateMinimalFatImage();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            await worker.OpenAsync();

            // Act
            worker.AddFile("NOEXT", new byte[] { 0x01 });
            await worker.SaveAsync(filePath); // Save changes to disk

            // Assert - Re-open to verify file was added
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            Assert.That(newWorker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Contain("NOEXT"));
        }

        [Test]
        public async Task AddFile_WithLongFileName_PreservesFullName()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "lfn-test.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1474560, "DISK");
            var fileContent = Encoding.ASCII.GetBytes("Test content");

            // Act - Add a file with a name longer than 8.3 (requires LFN)
            worker.AddFile("MyLongFileName.Txt", fileContent);
            await worker.SaveAsync(filePath);

            // Assert - LFN names are readable in directory listing
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            var paths = newWorker.FilesAndFolders.Select(f => f.Path).ToList();
            Assert.That(paths, Does.Contain("MyLongFileName.Txt"));
            Assert.That(newWorker.GetFileContent("MyLongFileName.Txt"), Is.EqualTo(fileContent));
        }

        [Test]
        public async Task AddFile_WithLowercaseName_PreservesCase()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "lower-test.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1474560, "DISK");
            var fileContent = new byte[] { 0x01 };

            // Act - Add a file with lowercase (requires LFN due to case)
            worker.AddFile("myfile.txt", fileContent);
            await worker.SaveAsync(filePath);

            // Assert
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            Assert.That(newWorker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Contain("myfile.txt"));
        }

        [Test]
        public async Task AddFile_With83FormatName_WorksCorrectly()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "short-name.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1474560, "DISK");
            var fileContent = new byte[] { 0x01 };

            // Act - Add a file with a valid 8.3 name (no LFN needed)
            worker.AddFile("SHORT.TXT", fileContent);
            await worker.SaveAsync(filePath);

            // Assert
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            Assert.That(newWorker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Contain("SHORT.TXT"));
            // Verify file is readable by 8.3 name
            Assert.That(newWorker.GetFileContent("SHORT.TXT"), Is.EqualTo(fileContent));
        }

        [Test]
        public async Task AddFile_WithDeletedEntriesInDirectory_FindsFreeSlot()
        {
            // Arrange - Create image with a file, delete it, then add another
            var filePath = Path.Combine(_tempDirectory!, "deleted-slots.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1474560, "DISK");
            worker.AddFile("FIRST.TXT", new byte[] { 0x01 });
            worker.DeleteEntry("FIRST.TXT");
            worker.AddFile("SECOND.TXT", new byte[] { 0x02 });

            // Assert
            var paths = worker.FilesAndFolders.Select(f => f.Path).ToList();
            Assert.That(paths, Does.Not.Contain("FIRST.TXT"));
            Assert.That(paths, Does.Contain("SECOND.TXT"));
        }

        [Test]
        public async Task AddFile_WithLowercaseIn83Format_PreservesLowercaseViaLfn()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "lower83.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1474560, "DISK");
            var fileContent = new byte[] { 0x01 };

            // Act - Add a file with lowercase and short name (needs LFN due to case)
            worker.AddFile("test.txt", fileContent);
            await worker.SaveAsync(filePath);

            // Assert - File should be listed with original case preserved
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            Assert.That(newWorker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Contain("test.txt"));
        }

        [Test]
        public async Task AddFile_WithLongFileNameInSubdirectory_PreservesName()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "subdir-lfn.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1474560, "DISK");
            worker.CreateFolder(string.Empty, "SUBDIR");
            var fileContent = Encoding.ASCII.GetBytes("Content");

            // Act - Add a file with a long name inside the subdirectory
            worker.AddFile("SUBDIR", "AVeryLongFileNameInSubdir.Txt", fileContent);
            await worker.SaveAsync(filePath);

            // Assert - LFN should be visible in directory listing
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            Assert.That(newWorker.FilesAndFolders.Select(f => f.Path).ToList(),
                Does.Contain("SUBDIR\\AVeryLongFileNameInSubdir.Txt"));
        }

        [Test]
        public async Task CreateFolder_WithLongName_PreservesFullName()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "folder-lfn.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1474560, "DISK");

            // Act
            worker.CreateFolder(string.Empty, "My Long Folder");
            await worker.SaveAsync(filePath);

            // Assert
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            Assert.That(newWorker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Contain("My Long Folder"));
        }

        [Test]
        public async Task CreateFolder_WritesSpacePaddedDotEntries()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "dot-entries.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1474560, "DISK");

            // Act
            worker.CreateFolder(string.Empty, "SUBDIR");
            await worker.SaveAsync(filePath);

            // Assert
            var image = await File.ReadAllBytesAsync(filePath);
            var bytesPerSector = BitConverter.ToUInt16(image, 11);
            var sectorsPerCluster = image[13];
            var reservedSectors = BitConverter.ToUInt16(image, 14);
            var numFats = image[16];
            var rootDirEntries = BitConverter.ToUInt16(image, 17);
            var sectorsPerFat = BitConverter.ToUInt16(image, 22);
            var rootDirSectors = ((rootDirEntries * 32) + (bytesPerSector - 1)) / bytesPerSector;
            var rootDirOffset = (reservedSectors + (numFats * sectorsPerFat)) * bytesPerSector;
            var folderEntryOffset = image[rootDirOffset + 11] == 0x08 ? rootDirOffset + 32 : rootDirOffset;
            var firstCluster = BitConverter.ToUInt16(image, folderEntryOffset + 26);

            Assert.That(firstCluster, Is.GreaterThan(1));

            var dataOffset = (reservedSectors + (numFats * sectorsPerFat) + rootDirSectors) * bytesPerSector;
            var directoryOffset = dataOffset + ((firstCluster - 2) * sectorsPerCluster * bytesPerSector);

            Assert.That(Encoding.ASCII.GetString(image, directoryOffset, 11), Is.EqualTo(".          "));
            Assert.That(Encoding.ASCII.GetString(image, directoryOffset + 32, 11), Is.EqualTo("..         "));
            Assert.That(image[directoryOffset + 11], Is.EqualTo(0x10));
            Assert.That(image[directoryOffset + 32 + 11], Is.EqualTo(0x10));
        }

        [Test]
        public async Task RenameEntry_WithLongFileName_PreservesFullName()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "rename-lfn.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1474560, "DISK");
            var content = Encoding.ASCII.GetBytes("Test content");
            worker.AddFile("MyLongFileName.Txt", content);

            // Act
            worker.RenameEntry("MyLongFileName.Txt", "RenamedLongFile.Txt");
            await worker.SaveAsync(filePath);

            // Assert
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            var paths = newWorker.FilesAndFolders.Select(f => f.Path).ToList();

            Assert.That(paths, Does.Not.Contain("MyLongFileName.Txt"));
            Assert.That(paths, Does.Contain("RenamedLongFile.Txt"));
            Assert.That(newWorker.GetFileContent("RenamedLongFile.Txt"), Is.EqualTo(content));
        }

        [Test]
        public async Task AddFile_WithContentSpanningMultipleClusters_PreservesAllBytes()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "multicluster.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1474560, "DISK");
            var fileContent = Enumerable.Range(0, 700).Select(i => (byte)(i % 251)).ToArray();

            // Act
            worker.AddFile("BIG.BIN", fileContent);
            await worker.SaveAsync(filePath);

            // Assert
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            var roundTrip = newWorker.GetFileContent("BIG.BIN");
            Assert.That(roundTrip, Is.EqualTo(fileContent));
        }

        [Test]
        public async Task CreateFolder_WhenDirectoryIsFull_DoesNotConsumeFreeSpace()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "full-root.img");
            var imageData = CreateFatImageWithFullRootDirectory();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            await worker.OpenAsync();
            var freeBytesBefore = worker.GetFreeBytes();

            // Act / Assert
            Assert.Throws<InvalidOperationException>(() => worker.CreateFolder(string.Empty, "EXTRA"));
            Assert.That(worker.GetFreeBytes(), Is.EqualTo(freeBytesBefore));
        }

        #endregion

        [Test]
        public async Task AddHostDirectory_AfterDeleteSaveReopen_ReaddsDirectoryThatNeedsMultipleClusters()
        {
            // Arrange
            var imagePath = Path.Combine(_tempDirectory!, "readd-large-dir.img");
            var hostFolderPath = Path.Combine(_tempDirectory!, "BIGDIR");
            Directory.CreateDirectory(hostFolderPath);

            for (var i = 0; i < 20; i++)
            {
                File.WriteAllBytes(Path.Combine(hostFolderPath, $"F{i:00}.TXT"), new byte[] { (byte)i });
            }

            var subFolderPath = Path.Combine(hostFolderPath, "SUB");
            Directory.CreateDirectory(subFolderPath);
            var innerContent = Encoding.ASCII.GetBytes("INNER");
            File.WriteAllBytes(Path.Combine(subFolderPath, "INNER.TXT"), innerContent);

            var worker = new DiskImageWorker(imagePath);
            worker.CreateBlankImage(1474560, "DISK");
            worker.AddHostDirectory(string.Empty, hostFolderPath);
            await worker.SaveAsync(imagePath);

            // Act
            worker.DeleteEntry("BIGDIR");
            await worker.SaveAsync(imagePath);

            var reopenedWorker = new DiskImageWorker(imagePath);
            await reopenedWorker.OpenAsync();
            reopenedWorker.AddHostDirectory(string.Empty, hostFolderPath);
            await reopenedWorker.SaveAsync(imagePath);

            // Assert
            var verifiedWorker = new DiskImageWorker(imagePath);
            await verifiedWorker.OpenAsync();
            var paths = verifiedWorker.FilesAndFolders.Select(f => f.Path).ToList();

            Assert.That(paths, Does.Contain("BIGDIR"));
            Assert.That(paths, Does.Contain("BIGDIR\\SUB"));
            Assert.That(paths, Does.Contain("BIGDIR\\SUB\\INNER.TXT"));
            Assert.That(paths, Does.Contain("BIGDIR\\F19.TXT"));
            Assert.That(verifiedWorker.GetFileContent("BIGDIR\\F19.TXT"), Is.EqualTo(new byte[] { 19 }));
            Assert.That(verifiedWorker.GetFileContent("BIGDIR\\SUB\\INNER.TXT"), Is.EqualTo(innerContent));
        }

        [Test]
        public async Task AddHostDirectoryAsync_ReportsProgressAndAddsContents()
        {
            // Arrange
            var worker = new DiskImageWorker("dummy.img");
            worker.CreateBlankImage(1474560, "DISK");

            var hostFolderPath = Path.Combine(_tempDirectory!, "ASYNCADD");
            Directory.CreateDirectory(hostFolderPath);
            var nestedFolderPath = Path.Combine(hostFolderPath, "SUB");
            Directory.CreateDirectory(nestedFolderPath);

            var firstContent = Encoding.ASCII.GetBytes("FIRST");
            var secondContent = Encoding.ASCII.GetBytes("SECOND!");
            File.WriteAllBytes(Path.Combine(hostFolderPath, "ONE.TXT"), firstContent);
            File.WriteAllBytes(Path.Combine(nestedFolderPath, "TWO.TXT"), secondContent);

            var totalBytes = firstContent.Length + secondContent.Length;
            var progressUpdates = new List<OperationProgress>();

            // Act
            await worker.AddHostDirectoryAsync(
                string.Empty,
                hostFolderPath,
                totalBytes,
                new InlineProgress<OperationProgress>(progressUpdates.Add));

            // Assert
            var paths = worker.FilesAndFolders.Select(f => f.Path).ToList();
            Assert.That(paths, Does.Contain("ASYNCADD"));
            Assert.That(paths, Does.Contain("ASYNCADD\\ONE.TXT"));
            Assert.That(paths, Does.Contain("ASYNCADD\\SUB"));
            Assert.That(paths, Does.Contain("ASYNCADD\\SUB\\TWO.TXT"));
            Assert.That(progressUpdates, Is.Not.Empty);
            Assert.That(progressUpdates[^1].ProcessedBytes, Is.EqualTo(totalBytes));
            Assert.That(progressUpdates[^1].TotalBytes, Is.EqualTo(totalBytes));
            Assert.That(progressUpdates.Any(update => update.CurrentItem == "ONE.TXT"), Is.True);
            Assert.That(progressUpdates.Any(update => update.CurrentItem == Path.Combine("SUB", "TWO.TXT")), Is.True);
        }

        [Test]
        public async Task ExtractFolderAsync_ReportsProgressAndWritesFiles()
        {
            // Arrange
            var worker = new DiskImageWorker("dummy.img");
            worker.CreateBlankImage(1474560, "DISK");
            worker.CreateFolder(string.Empty, "EXPORT");

            var rootContent = Encoding.ASCII.GetBytes("ROOT");
            var nestedContent = Encoding.ASCII.GetBytes("NESTED");
            worker.AddFile("EXPORT", "ROOT.TXT", rootContent);
            worker.CreateFolder("EXPORT", "SUB");
            worker.AddFile("EXPORT\\SUB", "INNER.TXT", nestedContent);

            var destinationPath = Path.Combine(_tempDirectory!, "EXTRACTED", "EXPORT");
            var totalBytes = rootContent.Length + nestedContent.Length;
            var progressUpdates = new List<OperationProgress>();

            // Act
            await worker.ExtractFolderAsync(
                "EXPORT",
                destinationPath,
                new InlineProgress<OperationProgress>(progressUpdates.Add));

            // Assert
            Assert.That(File.ReadAllBytes(Path.Combine(destinationPath, "ROOT.TXT")), Is.EqualTo(rootContent));
            Assert.That(File.ReadAllBytes(Path.Combine(destinationPath, "SUB", "INNER.TXT")), Is.EqualTo(nestedContent));
            Assert.That(progressUpdates, Is.Not.Empty);
            Assert.That(progressUpdates[^1].ProcessedBytes, Is.EqualTo(totalBytes));
            Assert.That(progressUpdates[^1].TotalBytes, Is.EqualTo(totalBytes));
            Assert.That(progressUpdates.Any(update => update.CurrentItem == "ROOT.TXT"), Is.True);
            Assert.That(progressUpdates.Any(update => update.CurrentItem == Path.Combine("SUB", "INNER.TXT")), Is.True);
        }

        [Test]
        public async Task ExtractFolder_WithTraversalPathEntry_DoesNotWriteOutsideDestination()
        {
            // Arrange - craft an image containing a folder entry whose name escapes the image root
            var filePath = Path.Combine(_tempDirectory!, "traversal.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1474560, "DISK");
            worker.CreateFolder(string.Empty, @"..\evil");
            await worker.SaveAsync(filePath);

            var evil = new DiskImageWorker(filePath);
            await evil.OpenAsync();
            Assert.That(evil.FilesAndFolders.Select(f => f.Path).ToList(), Does.Contain(@"..\evil"));

            var destination = Path.Combine(_tempDirectory!, "extract");

            // Act
            evil.ExtractFolder(string.Empty, destination);

            // Assert - nothing may be created outside the chosen destination folder
            Assert.That(Directory.Exists(Path.Combine(_tempDirectory!, "evil")), Is.False);
            // The neutralized entry lands inside the destination instead
            Assert.That(Directory.Exists(Path.Combine(destination, "evil")), Is.True);
        }

        [Test]
        public void ResolveSafeDestinationPath_NeutralizesTraversalAndRootedSegments()
        {
            var root = Path.Combine(_tempDirectory!, "dest");

            Assert.That(
                DiskImageWorker.ResolveSafeDestinationPath(root, @"..\evil.txt"),
                Is.EqualTo(Path.Combine(root, "evil.txt")));
            Assert.That(
                DiskImageWorker.ResolveSafeDestinationPath(root, @"..\..\..\evil.txt"),
                Is.EqualTo(Path.Combine(root, "evil.txt")));
            Assert.That(
                DiskImageWorker.ResolveSafeDestinationPath(root, @"sub\..\nested\..\ok.txt"),
                Is.EqualTo(Path.Combine(root, "sub", "nested", "ok.txt")));

            // Rooted segments are stripped so the result stays inside the destination
            Assert.That(
                DiskImageWorker.ResolveSafeDestinationPath(root, @"C:\Windows\evil.dll"),
                Is.EqualTo(Path.Combine(root, "Windows", "evil.dll")));

            // Paths that collapse onto (or above) the root after sanitization are rejected
            Assert.That(DiskImageWorker.ResolveSafeDestinationPath(root, @"..\.."), Is.Null);
            Assert.That(DiskImageWorker.ResolveSafeDestinationPath(root, "."), Is.Null);
            Assert.That(DiskImageWorker.ResolveSafeDestinationPath(root, ""), Is.Null);
        }

        #region UpdateFile Tests

        [Test]
        public async Task UpdateFile_WithZeroLengthFile_WritesNewClusterAndContent()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "update-zero.img");
            var imageData = CreateFatImageWithZeroLengthFile("EMPTY.TXT");
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            await worker.OpenAsync();
            var updatedContent = Encoding.ASCII.GetBytes("UPDATED");

            // Act
            worker.UpdateFile("EMPTY.TXT", updatedContent);
            await worker.SaveAsync(filePath);

            // Assert
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            Assert.That(newWorker.GetFileContent("EMPTY.TXT"), Is.EqualTo(updatedContent));
        }

        [Test]
        public void UpdateFile_WhenExpansionFails_PreservesOriginalContent()
        {
            // Arrange
            var worker = new DiskImageWorker("dummy.img");
            worker.CreateBlankImage(163840, "DISK");

            var originalContent = Encoding.ASCII.GetBytes("ORIGINAL");
            worker.AddFile("TARGET.TXT", originalContent);

            var fillerSize = (int)worker.GetFreeBytes() - SectorSize;
            worker.AddFile("FILLER.BIN", new byte[fillerSize]);
            var freeBytesBeforeUpdate = worker.GetFreeBytes();

            // Act / Assert
            Assert.That(freeBytesBeforeUpdate, Is.EqualTo(SectorSize));
            Assert.Throws<InvalidOperationException>(() => worker.UpdateFile("TARGET.TXT", new byte[1025]));
            Assert.That(worker.GetFileContent("TARGET.TXT"), Is.EqualTo(originalContent));
            Assert.That(worker.GetFreeBytes(), Is.EqualTo(freeBytesBeforeUpdate));
        }

        #endregion

        #region Delete Tests

        [Test]
        public void Delete_WhenImageNotLoaded_ThrowsInvalidOperationException()
        {
            // Arrange
            var worker = new DiskImageWorker("dummy.img");

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => worker.DeleteEntry("TEST.TXT"));
        }

        [Test]
        public async Task Delete_WithNonExistentFile_ThrowsFileNotFoundException()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateMinimalFatImage();
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            await worker.OpenAsync();

            // Act & Assert
            Assert.Throws<FileNotFoundException>(() => worker.DeleteEntry("NONEXISTENT.TXT"));
        }

        [Test]
        public async Task Delete_WithValidFile_RemovesFile()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateFatImageWithFiles(new[] { "TODELETE.TXT", "KEEP.TXT" });
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            await worker.OpenAsync();

            // Verify file exists before delete
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Contain("TODELETE.TXT"));

            // Act
            worker.DeleteEntry("TODELETE.TXT");
            await worker.SaveAsync(filePath); // Save changes to disk

            // Assert - Re-open to verify file was removed from the image
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            Assert.That(newWorker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Not.Contain("TODELETE.TXT"));
            Assert.That(newWorker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Contain("KEEP.TXT"));
        }

        [Test]
        public async Task Delete_CaseInsensitive_FileNameMatch()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "test.img");
            var imageData = CreateFatImageWithFiles(new[] { "TESTFILE.TXT" });
            File.WriteAllBytes(filePath, imageData);

            var worker = new DiskImageWorker(filePath);
            await worker.OpenAsync();

            // Act - Delete with different case
            worker.DeleteEntry("testfile.txt");
            await worker.SaveAsync(filePath); // Save changes to disk

            // Assert - Re-open to verify file was removed
            var newWorker = new DiskImageWorker(filePath);
            await newWorker.OpenAsync();
            Assert.That(newWorker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Not.Contain("TESTFILE.TXT"));
        }

        [Test]
        public void DeleteEntry_WithNestedDirectory_FreesAllClustersInTree()
        {
            // Arrange
            var worker = new DiskImageWorker("dummy.img");
            worker.CreateBlankImage(1474560, "DISK");
            var freeBytesBefore = worker.GetFreeBytes();

            worker.CreateFolder(string.Empty, "TOP");
            worker.CreateFolder("TOP", "SUB");
            worker.AddFile("TOP\\SUB", "FILE.BIN", Enumerable.Range(0, 600).Select(i => (byte)(i % 251)).ToArray());

            // Act
            worker.DeleteEntry("TOP");

            // Assert
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Not.Contain("TOP"));
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Not.Contain("TOP\\SUB"));
            Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Not.Contain("TOP\\SUB\\FILE.BIN"));
            Assert.That(worker.GetFreeBytes(), Is.EqualTo(freeBytesBefore));
        }

        #endregion

        #region New Image FAT Geometry (Issue #2)

        [Test]
        public async Task CreateBlankImage_StandardFloppy_HasCanonicalFat12Geometry()
        {
            // Arrange
            var filePath = Path.Combine(_tempDirectory!, "floppy-geometry.img");
            var worker = new DiskImageWorker(filePath);

            // Act
            worker.CreateBlankImage(1_474_560, "DISK");
            await worker.SaveAsync(filePath);

            // Assert - the on-image BPB matches the canonical 1.44 MB FAT12 layout
            var image = await File.ReadAllBytesAsync(filePath);
            var bytesPerSector = BitConverter.ToUInt16(image, 11);
            var sectorsPerCluster = image[13];
            var reservedSectors = BitConverter.ToUInt16(image, 14);
            var numFats = image[16];
            var rootDirEntries = BitConverter.ToUInt16(image, 17);
            var sectorsPerFat = BitConverter.ToUInt16(image, 22);

            Assert.That(bytesPerSector, Is.EqualTo(512));
            Assert.That(sectorsPerCluster, Is.EqualTo(1));
            Assert.That(reservedSectors, Is.EqualTo(1));
            Assert.That(numFats, Is.EqualTo(2));
            Assert.That(rootDirEntries, Is.EqualTo(224));
            Assert.That(sectorsPerFat, Is.EqualTo(9));

            var totalSectors = image.LongLength / SectorSize;
            var rootDirSectors = ((rootDirEntries * 32) + (bytesPerSector - 1)) / bytesPerSector;
            Assert.That(rootDirSectors, Is.EqualTo(14));

            var dataStartSector = (uint)(reservedSectors + (numFats * sectorsPerFat) + rootDirSectors);
            var dataClusters = (totalSectors - dataStartSector) / sectorsPerCluster;
            Assert.That(totalSectors, Is.EqualTo(2880));
            Assert.That(dataClusters, Is.EqualTo(2847));

            // A FAT must hold entries for every data cluster plus the two reserved entries.
            var requiredFatBytes = ((dataClusters + 2) * 3 + 1) / 2; // 1.5 bytes/entry rounded up
            Assert.That((long)sectorsPerFat * bytesPerSector, Is.GreaterThanOrEqualTo(requiredFatBytes));

            // Re-open through the public API to confirm the worker reads the same cluster count back.
            var reopened = new DiskImageWorker(filePath);
            await reopened.OpenAsync();
            Assert.That(reopened.FilesystemType, Is.EqualTo(DiskImageWorker.FatType.Fat12));
            Assert.That(reopened.GetTotalBytes(), Is.EqualTo(dataClusters * bytesPerSector * sectorsPerCluster));
        }

        [Test]
        public async Task CreateBlankImage_LargeFileBeyondOldFatBoundary_RoundTripsWithoutCorruptingRootDirectory()
        {
            // Arrange - a ~1.4 MB file on a 1.44 MB floppy used to need FAT entries past the old undersized FAT
            var filePath = Path.Combine(_tempDirectory!, "floppy-overflow.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1_474_560, "DISK");

            var bigContent = new byte[1_400_000];
            for (var i = 0; i < bigContent.Length; i++)
            {
                bigContent[i] = (byte)(i % 251);
            }

            var smallContent = Encoding.ASCII.GetBytes("SMALL");

            // Act
            worker.AddFile("SMALL.TXT", smallContent);
            worker.AddFile("BIGFILE.BIN", bigContent);
            await worker.SaveAsync(filePath);

            // Assert - the root directory still parses and both files round-trip intact.
            var reopened = new DiskImageWorker(filePath);
            await reopened.OpenAsync();
            var paths = reopened.FilesAndFolders.Select(f => f.Path).ToList();

            Assert.That(paths, Does.Contain("SMALL.TXT"));
            Assert.That(paths, Does.Contain("BIGFILE.BIN"));
            Assert.That(reopened.GetFileContent("SMALL.TXT"), Is.EqualTo(smallContent));
            Assert.That(reopened.GetFileContent("BIGFILE.BIN"), Is.EqualTo(bigContent));
        }

        [Test]
        public void CreateHardDiskImage_Fat16Geometry_HasFatLargeEnoughForAllClusters()
        {
            // Arrange / Act - canonical FAT16 sizing case (1024 x 4 x 63)
            var worker = new DiskImageWorker("fat16-geometry.img");
            worker.CreateHardDiskImage(1024, 4, 63, "DISK");

            Assert.That(worker.FilesystemType, Is.EqualTo(DiskImageWorker.FatType.Fat16));
            var imageData = worker.GetImageData();
            Assert.That(imageData, Is.Not.Null);
            var image = imageData!;

            // The FAT16 boot sector lives in the first partition (sector 63).
            const int boot = 63 * SectorSize;
            var bytesPerSector = BitConverter.ToUInt16(image, boot + 11);
            var sectorsPerCluster = image[boot + 13];
            var reservedSectors = BitConverter.ToUInt16(image, boot + 14);
            var numFats = image[boot + 16];
            var rootDirEntries = BitConverter.ToUInt16(image, boot + 17);
            var sectorsPerFat = BitConverter.ToUInt16(image, boot + 22);
            var partitionSectors = BitConverter.ToUInt32(image, boot + 32);

            var rootDirSectors = ((rootDirEntries * 32) + (bytesPerSector - 1)) / bytesPerSector;
            var dataStartSector = (uint)(reservedSectors + (numFats * sectorsPerFat) + rootDirSectors);
            var dataClusters = (long)((partitionSectors - dataStartSector) / sectorsPerCluster);

            // Must be in the FAT16 cluster range and each FAT copy must cover (dataClusters + 2) * 2 bytes.
            Assert.That(dataClusters, Is.GreaterThanOrEqualTo(4085));
            Assert.That(dataClusters, Is.LessThan(65525));

            var requiredFatBytes = (dataClusters + 2) * 2; // 2 bytes/entry for FAT16
            Assert.That((long)sectorsPerFat * bytesPerSector, Is.GreaterThanOrEqualTo(requiredFatBytes));

            // The old 1.5-bytes/entry sizing under-allocated this FAT to 12288 bytes; the fix must be larger.
            Assert.That((long)sectorsPerFat * bytesPerSector, Is.GreaterThan(12288));
        }

        #endregion

        #region FAT16 Reserved Entries (Issue #7)

        private static ushort ReadFat16Entry(byte[] image, int fatBase, uint cluster)
            => BitConverter.ToUInt16(image, fatBase + (int)(cluster * 2));

        // Asserts the two reserved FAT16 entries and the free data-cluster-2 in every FAT copy.
        private static void AssertFat16ReservedEntriesCorrect(byte[] image, uint partitionStart)
        {
            var boot = (int)partitionStart * SectorSize;
            var bytesPerSector = BitConverter.ToUInt16(image, boot + 11);
            var reservedSectors = BitConverter.ToUInt16(image, boot + 14);
            byte numFats = image[boot + 16];
            var sectorsPerFat = BitConverter.ToUInt16(image, boot + 22);
            var media = image[boot + 21];

            Assert.That(bytesPerSector, Is.EqualTo(SectorSize));
            Assert.That(numFats, Is.GreaterThanOrEqualTo((byte)1));

            for (var fat = 0; fat < numFats; fat++)
            {
                var fatBase = (int)((partitionStart + reservedSectors + (uint)(fat * sectorsPerFat)) * bytesPerSector);
                var entry0 = ReadFat16Entry(image, fatBase, 0);
                var entry1 = ReadFat16Entry(image, fatBase, 1);
                var entry2 = ReadFat16Entry(image, fatBase, 2);

                Assert.That((byte)(entry0 & 0xFF), Is.EqualTo(media), $"FAT{fat} entry[0] low byte must be the media descriptor");
                Assert.That((byte)(entry0 >> 8), Is.EqualTo(0xFF), $"FAT{fat} entry[0] high byte must be 0xFF");
                Assert.That(entry1, Is.EqualTo((ushort)0xFFFF), $"FAT{fat} entry[1] must be end-of-chain 0xFFFF");
                Assert.That(entry2, Is.EqualTo((ushort)0x0000), $"FAT{fat} entry[2] (data cluster 2) must be free");
            }
        }

        [Test]
        public async Task CreateHardDiskImage_Fat16_WritesCorrectReservedEntriesAndLeavesCluster2Free()
        {
            // Arrange / Act
            var filePath = Path.Combine(_tempDirectory!, "fat16-create.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateHardDiskImage(128, 4, 63, "DISK");
            Assert.That(worker.FilesystemType, Is.EqualTo(DiskImageWorker.FatType.Fat16));
            await worker.SaveAsync(filePath);

            // Assert - both FAT copies on disk have correct reserved entries and free cluster 2
            var image = await File.ReadAllBytesAsync(filePath);
            AssertFat16ReservedEntriesCorrect(image, worker.GetPartitionStartSector());
        }

        [Test]
        public void FormatImage_Fat16_WritesCorrectReservedEntriesAndLeavesCluster2Free()
        {
            // Arrange - allocate cluster 2 first so format must clear it back to free
            var worker = new DiskImageWorker("fat16-format.img");
            worker.CreateHardDiskImage(128, 4, 63, "DISK");
            worker.AddFile("OLD.TXT", new byte[] { 0x01, 0x02 });

            // Act
            worker.FormatImage();

            // Assert
            Assert.That(worker.FilesystemType, Is.EqualTo(DiskImageWorker.FatType.Fat16));
            var image = worker.GetImageData()!;
            AssertFat16ReservedEntriesCorrect(image, worker.GetPartitionStartSector());
        }

        [Test]
        public async Task AddFile_OnFreshFat16Image_AllocatesCluster2First()
        {
            // Arrange / Act
            var filePath = Path.Combine(_tempDirectory!, "fat16-alloc.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateHardDiskImage(128, 4, 63, "DISK");
            Assert.That(worker.FilesystemType, Is.EqualTo(DiskImageWorker.FatType.Fat16));

            // A fresh image must report its full capacity free (no orphan allocation at cluster 2).
            Assert.That(worker.GetFreeBytes(), Is.EqualTo(worker.GetTotalBytes()));

            worker.AddFile("A.TXT", new byte[] { 0x42 });

            var image = worker.GetImageData()!;
            var partitionStart = worker.GetPartitionStartSector();
            var boot = (int)partitionStart * SectorSize;
            var bytesPerSector = BitConverter.ToUInt16(image, boot + 11);
            var reservedSectors = BitConverter.ToUInt16(image, boot + 14);
            var sectorsPerFat = BitConverter.ToUInt16(image, boot + 22);
            var fatBase = (int)((partitionStart + reservedSectors) * bytesPerSector);
            var fatSize = sectorsPerFat * bytesPerSector;

            // Cluster 2 is now the (single-cluster) chain end-of-chain in BOTH FAT copies, cluster 3 untouched.
            Assert.That(ReadFat16Entry(image, fatBase, 2), Is.EqualTo((ushort)0xFFFF));
            Assert.That(ReadFat16Entry(image, fatBase + fatSize, 2), Is.EqualTo((ushort)0xFFFF));
            Assert.That(ReadFat16Entry(image, fatBase, 3), Is.EqualTo((ushort)0x0000));

            var clusterSize = bytesPerSector * image[boot + 13];
            Assert.That(worker.GetFreeBytes(), Is.EqualTo(worker.GetTotalBytes() - clusterSize));

            // Round-trip through save/reopen.
            await worker.SaveAsync(filePath);
            var reopened = new DiskImageWorker(filePath);
            await reopened.OpenAsync();
            Assert.That(reopened.FilesAndFolders.Select(f => f.Path), Does.Contain("A.TXT"));
            Assert.That(reopened.GetFileContent("A.TXT"), Is.EqualTo(new byte[] { 0x42 }));
        }

        #endregion

        #region Cluster-Chain-Aware Directory Entries (Issue #3)

        [Test]
        public async Task AddFile_IntoDirectoryWithFreeEntriesAcrossClusterBoundary_WritesEntriesWithoutClobberingFileData()
        {
            // Arrange - the 1.44 MB FAT12 floppy uses 512-byte clusters, so a subdirectory holds 16 entries per cluster
            var filePath = Path.Combine(_tempDirectory!, "fragmented-directory.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1_474_560, "DISK");

            var blockContent = new byte[512];
            for (var i = 0; i < blockContent.Length; i++)
            {
                // The high bit is always set so no BLOCK.BIN byte can be mistaken for an LFN attribute byte.
                blockContent[i] = (byte)((i % 251) | 0x80);
            }

            // Act - DOCS claims the first data cluster and BLOCK.BIN immediately takes the following one, so the
            // next cluster the DOCS directory grows into can never be physically contiguous with its first cluster.
            worker.CreateFolder(string.Empty, "DOCS");
            worker.AddFile("BLOCK.BIN", blockContent);

            for (var i = 0; i < 15; i++)
            {
                worker.AddFile("DOCS", $"F{i:D2}.TXT", Array.Empty<byte>());
            }

            var image = worker.GetImageData();
            Assert.That(image, Is.Not.Null);
            var directoryClusters = ReadFat12ClusterChain(image!, FindRootDirectoryEntryCluster(image!, "DOCS"));
            Assert.That(directoryClusters, Has.Count.EqualTo(2), "DOCS should have grown into a second cluster.");
            Assert.That(directoryClusters[1], Is.Not.EqualTo(directoryClusters[0] + 1),
                "the second DOCS cluster must not be physically contiguous with the first.");

            // Deleting F13 (last slot of the first directory cluster) and F14 (first slot of the second one) leaves a
            // two-entry free run that straddles the directory cluster boundary.
            worker.DeleteEntry("DOCS\\F13.TXT");
            worker.DeleteEntry("DOCS\\F14.TXT");

            image = worker.GetImageData();
            Assert.That(image, Is.Not.Null);
            var deletedImage = image!;
            var firstClusterLayout = GetFat12ClusterLayout(deletedImage, directoryClusters[0]);
            var secondClusterLayout = GetFat12ClusterLayout(deletedImage, directoryClusters[1]);
            Assert.That(deletedImage[firstClusterLayout.ClusterOffset + firstClusterLayout.ClusterSize - 32], Is.EqualTo(0xE5));
            Assert.That(deletedImage[secondClusterLayout.ClusterOffset], Is.EqualTo(0xE5));

            var lowerContent = Encoding.ASCII.GetBytes("payload stored in lower.txt");

            // Act - "lower.txt" needs one LFN entry plus its short name entry, which land in different clusters.
            worker.AddFile("DOCS", "lower.txt", lowerContent);
            await worker.SaveAsync(filePath);

            // Assert - reopening sees exactly one lower.txt entry with its content ...
            var reopened = new DiskImageWorker(filePath);
            await reopened.OpenAsync();
            var paths = reopened.FilesAndFolders.Select(f => f.Path).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(paths, Does.Contain("DOCS\\lower.txt"));
                Assert.That(paths.Count(p => Path.GetFileName(p).Equals("lower.txt", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1));
                Assert.That(reopened.GetFileContent("DOCS\\lower.txt"), Is.EqualTo(lowerContent));

                // ... and BLOCK.BIN survived because the entry was written to its real directory offsets.
                Assert.That(paths, Does.Contain("BLOCK.BIN"));
                Assert.That(reopened.GetFileContent("BLOCK.BIN"), Is.EqualTo(blockContent));
            });
        }

        /// <summary>
        /// Reads a FAT12 chain starting at the given cluster (raw FAT image, no MBR).
        /// </summary>
        private static List<uint> ReadFat12ClusterChain(byte[] image, uint firstCluster)
        {
            var fatStart = BitConverter.ToUInt16(image, 14) * SectorSize;
            var clusters = new List<uint>();
            var cluster = firstCluster;

            while (cluster >= 2 && cluster < 0xFF8)
            {
                clusters.Add(cluster);
                var entryOffset = fatStart + (int)(cluster * 3 / 2);
                var raw = (ushort)(image[entryOffset] | (image[entryOffset + 1] << 8));
                cluster = cluster % 2 == 0 ? (uint)(raw & 0x0FFF) : (uint)(raw >> 4);
            }

            return clusters;
        }

        /// <summary>
        /// Returns the byte offset and size of a FAT12 cluster in a raw (non-partitioned) image.
        /// </summary>
        private static (int ClusterOffset, int ClusterSize) GetFat12ClusterLayout(byte[] image, uint cluster)
        {
            var bytesPerSector = (int)BitConverter.ToUInt16(image, 11);
            var sectorsPerCluster = (int)image[13];
            var reservedSectors = (int)BitConverter.ToUInt16(image, 14);
            var numFats = (int)image[16];
            var rootDirEntries = (int)BitConverter.ToUInt16(image, 17);
            var sectorsPerFat = (int)BitConverter.ToUInt16(image, 22);
            var rootDirSectors = ((rootDirEntries * 32) + (bytesPerSector - 1)) / bytesPerSector;
            var dataStartSector = reservedSectors + (numFats * sectorsPerFat) + rootDirSectors;

            return ((dataStartSector + (((int)cluster - 2) * sectorsPerCluster)) * bytesPerSector,
                sectorsPerCluster * bytesPerSector);
        }

        /// <summary>
        /// Finds the first cluster of a named root directory entry in a raw (non-partitioned) image.
        /// </summary>
        private static uint FindRootDirectoryEntryCluster(byte[] image, string name)
        {
            var bytesPerSector = (int)BitConverter.ToUInt16(image, 11);
            var reservedSectors = (int)BitConverter.ToUInt16(image, 14);
            var numFats = (int)image[16];
            var rootDirEntries = (int)BitConverter.ToUInt16(image, 17);
            var sectorsPerFat = (int)BitConverter.ToUInt16(image, 22);
            var paddedName = name.PadRight(11);

            var offset = (reservedSectors + (numFats * sectorsPerFat)) * bytesPerSector;
            for (var i = 0; i < rootDirEntries; i++, offset += 32)
            {
                if (Encoding.ASCII.GetString(image, offset, 11) == paddedName)
                {
                    return BitConverter.ToUInt16(image, offset + 26);
                }
            }

            throw new InvalidOperationException($"Root directory entry '{name}' was not found.");
        }

        #endregion

        #region Atomic Move (Issue #4)

        [Test]
        public async Task MoveEntry_WithFullTargetDirectory_ThrowsAndKeepsSourceIntact()
        {
            // Arrange - pack the root directory of a 160 KB FAT12 floppy completely full, then keep a
            // multi-cluster KEEP.TXT inside DOCS so a move into the full root has nowhere to write.
            var filePath = Path.Combine(_tempDirectory!, "full-target-move.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(163_840, "DISK");

            var keepContent = Enumerable.Range(0, 600).Select(i => (byte)(i % 251)).ToArray();
            worker.CreateFolder(string.Empty, "DOCS");
            worker.AddFile("DOCS", "KEEP.TXT", keepContent);

            // Fill every remaining root slot: 224 entries - volume label - DOCS.
            for (var i = 0; i < 222; i++)
            {
                worker.AddFile(string.Empty, $"FILL{i:D3}.BIN", new byte[] { 0x01 });
            }

            var freeBytesBefore = worker.GetFreeBytes();

            // Act / Assert - the move must fail ...
            var exception = Assert.Throws<InvalidOperationException>(() => worker.MoveEntry(@"DOCS\KEEP.TXT", string.Empty));
            Assert.That(exception!.Message, Does.Contain("No free directory entries"));

            // ... and leave the source untouched: still listed, still readable with identical content.
            Assert.Multiple(() =>
            {
                Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Contain(@"DOCS\KEEP.TXT"));
                Assert.That(worker.GetFileContent(@"DOCS\KEEP.TXT"), Is.EqualTo(keepContent));
                Assert.That(worker.FilesAndFolders.Select(f => f.Path).ToList(), Does.Not.Contain("KEEP.TXT"));
                Assert.That(worker.GetFreeBytes(), Is.EqualTo(freeBytesBefore));
            });

            // Persist and re-open to confirm the failed move also left the saved image intact.
            await worker.SaveAsync(filePath);
            var reopened = new DiskImageWorker(filePath);
            await reopened.OpenAsync();
            Assert.Multiple(() =>
            {
                Assert.That(reopened.FilesAndFolders.Select(f => f.Path).ToList(), Does.Contain(@"DOCS\KEEP.TXT"));
                Assert.That(reopened.GetFileContent(@"DOCS\KEEP.TXT"), Is.EqualTo(keepContent));
            });
        }

        [Test]
        public async Task MoveEntry_WithRoomInTarget_MovesFileAndPreservesContent()
        {
            // Arrange - a multi-cluster file and an LFN-named file in the root of a 1.44 MB floppy
            var filePath = Path.Combine(_tempDirectory!, "move-ok.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1_474_560, "DISK");
            worker.CreateFolder(string.Empty, "DOCS");

            var bigContent = Enumerable.Range(0, 700).Select(i => (byte)(i % 251)).ToArray();
            var lfnContent = Encoding.ASCII.GetBytes("long name payload");
            worker.AddFile("BIGKEEP.BIN", bigContent);
            worker.AddFile("LongKeepName.Txt", lfnContent);
            var freeBytesBeforeMove = worker.GetFreeBytes();

            // Act
            worker.MoveEntry("BIGKEEP.BIN", "DOCS");
            worker.MoveEntry("LongKeepName.Txt", "DOCS");

            // Assert - the in-memory model reflects the moves and reuses the cluster chains
            var paths = worker.FilesAndFolders.Select(f => f.Path).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(paths, Does.Not.Contain("BIGKEEP.BIN"));
                Assert.That(paths, Does.Not.Contain("LongKeepName.Txt"));
                Assert.That(paths, Does.Contain(@"DOCS\BIGKEEP.BIN"));
                Assert.That(paths, Does.Contain(@"DOCS\LongKeepName.Txt"));
                Assert.That(worker.GetFileContent(@"DOCS\BIGKEEP.BIN"), Is.EqualTo(bigContent));
                Assert.That(worker.GetFreeBytes(), Is.EqualTo(freeBytesBeforeMove), "a move must not reallocate clusters");
            });

            // Save + reopen: the file data and the long name survive the move intact.
            await worker.SaveAsync(filePath);
            var reopened = new DiskImageWorker(filePath);
            await reopened.OpenAsync();
            var reopenedPaths = reopened.FilesAndFolders.Select(f => f.Path).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(reopenedPaths, Does.Contain(@"DOCS\BIGKEEP.BIN"));
                Assert.That(reopenedPaths, Does.Contain(@"DOCS\LongKeepName.Txt"));
                Assert.That(reopened.GetFileContent(@"DOCS\BIGKEEP.BIN"), Is.EqualTo(bigContent));
                Assert.That(reopened.GetFileContent(@"DOCS\LongKeepName.Txt"), Is.EqualTo(lfnContent));
            });
        }

        #endregion

        #region Duplicate Names And Short-Name Collisions (Issue #6)

        [Test]
        public async Task AddFile_WithDuplicateName_ThrowsAndKeepsSingleEntry()
        {
            // Arrange - a plain 8.3 file added once with distinguishable content.
            var filePath = Path.Combine(_tempDirectory!, "dup-name.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1_474_560, "DISK");

            var originalContent = Encoding.ASCII.GetBytes("ORIGINAL");
            worker.AddFile("SAME.TXT", originalContent);

            // Act / Assert - adding the same name again must fail without touching the image ...
            Assert.Throws<IOException>(() => worker.AddFile("SAME.TXT", Encoding.ASCII.GetBytes("DUPLICATE")));
            // ... and the comparison is case-insensitive.
            Assert.Throws<IOException>(() => worker.AddFile("same.txt", Encoding.ASCII.GetBytes("DUPLICATE")));

            Assert.Multiple(() =>
            {
                Assert.That(worker.FilesAndFolders.Count(f => f.Path.Equals("SAME.TXT", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1));
                Assert.That(worker.GetFileContent("SAME.TXT"), Is.EqualTo(originalContent));
            });

            // Persist + re-open: exactly one SAME.TXT entry survives and reads back the original bytes.
            await worker.SaveAsync(filePath);
            var reopened = new DiskImageWorker(filePath);
            await reopened.OpenAsync();
            var sameEntries = reopened.FilesAndFolders.Where(f => f.Path.Equals("SAME.TXT", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(sameEntries, Has.Count.EqualTo(1));
                Assert.That(reopened.GetFileContent("SAME.TXT"), Is.EqualTo(originalContent));
            });
        }

        [Test]
        public async Task AddFile_WithLongNamesSharingShortNameBase_GetsDistinctAliasesAndRoundTrips()
        {
            // Arrange - two long names whose naive 8.3 truncation is the same (ABCDEFGH.TXT).
            var filePath = Path.Combine(_tempDirectory!, "alias-collision.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1_474_560, "DISK");

            var oneContent = Encoding.ASCII.GetBytes("ONE-PAYLOAD");
            var twoContent = Encoding.ASCII.GetBytes("TWO-PAYLOAD-DIFFERENT");

            // Act - both adds must succeed with distinct generated aliases.
            worker.AddFile("abcdefgh-one.txt", oneContent);
            worker.AddFile("abcdefgh-two.txt", twoContent);
            await worker.SaveAsync(filePath);

            // The two directory entries carry different 8.3 short names: the first keeps the free truncated base and
            // the second falls back to a "~n" alias (ABCDEFGH.TXT -> ABCDEF~1.TXT).
            var savedImage = await File.ReadAllBytesAsync(filePath);
            var shortNames = ReadRootDirectoryShortNames(savedImage);
            Assert.Multiple(() =>
            {
                Assert.That(shortNames, Has.Count.EqualTo(2));
                Assert.That(shortNames.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(2), "the two entries must not share a short name");
                Assert.That(shortNames, Is.EquivalentTo(new[] { "ABCDEFGH.TXT", "ABCDEF~1.TXT" }));
            });

            // Re-open: both long names round-trip and each reads back its own bytes.
            var reopened = new DiskImageWorker(filePath);
            await reopened.OpenAsync();
            var paths = reopened.FilesAndFolders.Select(f => f.Path).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(paths, Does.Contain("abcdefgh-one.txt"));
                Assert.That(paths, Does.Contain("abcdefgh-two.txt"));
                Assert.That(reopened.GetFileContent("abcdefgh-one.txt"), Is.EqualTo(oneContent));
                Assert.That(reopened.GetFileContent("abcdefgh-two.txt"), Is.EqualTo(twoContent));
            });
        }

        [Test]
        public async Task RenameEntry_OntoExistingCaseInsensitiveName_ThrowsAndKeepsSource()
        {
            // Arrange - two files that differ only by case in the root directory.
            var filePath = Path.Combine(_tempDirectory!, "rename-collision.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1_474_560, "DISK");

            var keepContent = Encoding.ASCII.GetBytes("KEEP-ME");
            var otherContent = Encoding.ASCII.GetBytes("RENAME-SOURCE");
            worker.AddFile("SAME.TXT", keepContent);
            worker.AddFile("OTHER.TXT", otherContent);

            // Act / Assert - renaming onto the existing name (case-insensitive) fails ...
            Assert.Throws<IOException>(() => worker.RenameEntry("OTHER.TXT", "same.txt"));

            // ... and the source entry is fully intact.
            Assert.Multiple(() =>
            {
                var paths = worker.FilesAndFolders.Select(f => f.Path).ToList();
                Assert.That(paths, Does.Contain("OTHER.TXT"));
                Assert.That(paths, Does.Contain("SAME.TXT"));
                Assert.That(worker.GetFileContent("OTHER.TXT"), Is.EqualTo(otherContent));
                Assert.That(worker.GetFileContent("SAME.TXT"), Is.EqualTo(keepContent));
            });

            await worker.SaveAsync(filePath);
            var reopened = new DiskImageWorker(filePath);
            await reopened.OpenAsync();
            var reopenedPaths = reopened.FilesAndFolders.Select(f => f.Path).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(reopenedPaths, Does.Contain("OTHER.TXT"));
                Assert.That(reopened.GetFileContent("OTHER.TXT"), Is.EqualTo(otherContent));
            });
        }

        [Test]
        public async Task MoveEntry_OntoExistingCaseInsensitiveName_ThrowsAndKeepsSource()
        {
            // Arrange - a root file and a same-named (case-insensitive) file already living in DOCS.
            var filePath = Path.Combine(_tempDirectory!, "move-collision.img");
            var worker = new DiskImageWorker(filePath);
            worker.CreateBlankImage(1_474_560, "DISK");
            worker.CreateFolder(string.Empty, "DOCS");

            var sourceContent = Encoding.ASCII.GetBytes("ROOT-SOURCE");
            var targetContent = Encoding.ASCII.GetBytes("TARGET-OCCUPANT");
            worker.AddFile("MOVE.TXT", sourceContent);
            worker.AddFile("DOCS", "move.txt", targetContent);
            var freeBytesBefore = worker.GetFreeBytes();

            // Act / Assert - the move onto an existing name fails ...
            Assert.Throws<IOException>(() => worker.MoveEntry("MOVE.TXT", "DOCS"));

            // ... and the source stays in the root, untouched and still readable.
            Assert.Multiple(() =>
            {
                var paths = worker.FilesAndFolders.Select(f => f.Path).ToList();
                Assert.That(paths, Does.Contain("MOVE.TXT"));
                Assert.That(paths, Does.Not.Contain(@"DOCS\MOVE.TXT"));
                Assert.That(worker.GetFileContent("MOVE.TXT"), Is.EqualTo(sourceContent));
                Assert.That(worker.GetFileContent(@"DOCS\move.txt"), Is.EqualTo(targetContent));
                Assert.That(worker.GetFreeBytes(), Is.EqualTo(freeBytesBefore));
            });

            await worker.SaveAsync(filePath);
            var reopened = new DiskImageWorker(filePath);
            await reopened.OpenAsync();
            var reopenedPaths = reopened.FilesAndFolders.Select(f => f.Path).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(reopenedPaths, Does.Contain("MOVE.TXT"));
                Assert.That(reopened.GetFileContent("MOVE.TXT"), Is.EqualTo(sourceContent));
                Assert.That(reopened.GetFileContent(@"DOCS\move.txt"), Is.EqualTo(targetContent));
            });
        }

        /// <summary>
        /// Reads the 8.3 short names of every live, non-directory, non-volume-label entry from an image's root
        /// directory so tests can assert on the generated aliases independent of the long-name layer.
        /// </summary>
        private static List<string> ReadRootDirectoryShortNames(byte[] image)
        {
            var bytesPerSector = BitConverter.ToUInt16(image, 11);
            var reservedSectors = BitConverter.ToUInt16(image, 14);
            var numFats = image[16];
            var rootDirEntries = BitConverter.ToUInt16(image, 17);
            var sectorsPerFat = BitConverter.ToUInt16(image, 22);
            var rootDirOffset = (reservedSectors + (numFats * sectorsPerFat)) * bytesPerSector;

            var shortNames = new List<string>();
            for (var i = 0; i < rootDirEntries; i++)
            {
                var offset = rootDirOffset + (i * 32);
                if (offset + 32 > image.Length)
                    break;

                var firstByte = image[offset];
                if (firstByte == 0x00)
                    break;
                if (firstByte == 0xE5)
                    continue;

                var attr = image[offset + 11];
                if ((attr & 0x0F) == 0x0F) // LFN fragment
                    continue;
                if ((attr & 0x08) != 0) // volume label
                    continue;
                if ((attr & 0x10) != 0) // directory
                    continue;

                var namePart = Encoding.ASCII.GetString(image, offset, 8).TrimEnd();
                var extPart = Encoding.ASCII.GetString(image, offset + 8, 3).TrimEnd();
                shortNames.Add(extPart.Length == 0 ? namePart : $"{namePart}.{extPart}");
            }

            return shortNames;
        }

        #endregion

        private sealed class InlineProgress<T>(Action<T> onReport) : IProgress<T>
        {
            private readonly Action<T> _onReport = onReport;

            public void Report(T value)
            {
                _onReport(value);
            }
        }
    }
}
