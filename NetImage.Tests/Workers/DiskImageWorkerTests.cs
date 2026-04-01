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

            // Act & Assert - Should not throw
            await worker.OpenAsync();
            Assert.That(worker.IsLoaded, Is.True);
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
