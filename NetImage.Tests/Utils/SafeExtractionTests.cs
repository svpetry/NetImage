using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NetImage.Utils;
using NUnit.Framework;

namespace NetImage.Tests.Utils
{
    [TestFixture]
    public class SafeExtractionTests
    {
        private string _tempDirectory = null!;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "NetImageExtractionTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown() => DeleteDirectory(_tempDirectory);

        private static void DeleteDirectory(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            {
                foreach (var directory in Directory.GetDirectories(path))
                    DeleteDirectory(directory);
                foreach (var file in Directory.GetFiles(path))
                    File.Delete(file);
            }
            Directory.Delete(path);
        }

        [Test]
        public void DirectoryLease_PreventsReplacingBoundaryAndAncestorsUntilDisposed()
        {
            var parent = Path.Combine(_tempDirectory, "parent");
            var boundary = Path.Combine(parent, "extract");
            var child = Path.Combine(boundary, "SUB");
            var movedParent = Path.Combine(_tempDirectory, "moved-parent");
            using (var directories = SafeExtraction.TryAcquireDirectoryChain(boundary, child))
            {
                Assert.That(directories, Is.Not.Null);
                Assert.Throws<IOException>(() => Directory.Move(parent, movedParent));
                Assert.Throws<IOException>(() => Directory.Move(boundary, Path.Combine(parent, "moved-extract")));
                Assert.Throws<IOException>(() => Directory.Move(child, Path.Combine(boundary, "moved-sub")));
            }

            // Successful rename afterward also verifies that validation did not leak handles.
            Directory.Move(parent, movedParent);
            Assert.That(Directory.Exists(Path.Combine(movedParent, "extract", "SUB")), Is.True);
        }

        [TestCase(0u)]
        [TestCase(0x100u)] // FILE_WRITE_ATTRIBUTES
        [TestCase(0x40000000u)] // GENERIC_WRITE
        public void DirectoryLease_ReparseMutationCannotRedirectFileCreation(uint desiredAccess)
        {
            var boundary = Path.Combine(_tempDirectory, "extract");
            var child = Path.Combine(boundary, "SUB");
            var outside = Path.Combine(_tempDirectory, "outside");
            Directory.CreateDirectory(outside);
            var outsideFile = Path.Combine(outside, "KEEP.TXT");
            File.WriteAllText(outsideFile, "original");
            using var directories = SafeExtraction.TryAcquireDirectoryChain(boundary, child);
            Assert.That(directories, Is.Not.Null);
            using var attacker = NativeMethods.CreateFile(child, desiredAccess, 7, IntPtr.Zero,
                3, 0x02000000 | 0x00200000, IntPtr.Zero);
            if (attacker.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                Assert.That(error, Is.EqualTo(32), "write/delete access must conflict with the extraction lease");
                return;
            }

            var pathBytes = Encoding.Unicode.GetBytes("\\??\\" + outside + "\0" + outside + "\0");
            var substituteLength = Encoding.Unicode.GetByteCount("\\??\\" + outside);
            var printLength = Encoding.Unicode.GetByteCount(outside);
            var buffer = new byte[16 + pathBytes.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substituteLength));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substituteLength + 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)printLength));
            pathBytes.CopyTo(buffer, 16);

            var changed = NativeMethods.DeviceIoControl(attacker, 0x000900A4, buffer, buffer.Length,
                IntPtr.Zero, 0, out _, IntPtr.Zero); // FSCTL_SET_REPARSE_POINT
            var mutationError = Marshal.GetLastWin32Error();

            if (!changed)
            {
                Assert.That(mutationError, Is.EqualTo(5));
                return;
            }

            // Attribute-only handles can set reparse data despite sharing restrictions. Opening relative to the
            // already-held directory must still never follow the new junction, even when creating a missing file.
            foreach (var name in new[] { "KEEP.TXT", "NEW.BIN" })
            {
                using var file = SafeExtraction.TryOpenFile(directories!, name);
                if (file != null)
                {
                    using var stream = new FileStream(file, FileAccess.Write);
                    stream.WriteByte(42);
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(outsideFile), Is.EqualTo("original"));
                Assert.That(File.Exists(Path.Combine(outside, "NEW.BIN")), Is.False);
            });
        }

        [TestCase(0)]
        [TestCase(3)]
        public void TryWriteAllBytesWithinRoot_OverwritesAndSetsTimestampThroughValidatedHandle(int length)
        {
            var boundary = Path.Combine(_tempDirectory, "extract");
            Directory.CreateDirectory(boundary);
            var target = Path.Combine(boundary, "FILE.BIN");
            File.WriteAllText(target, "old contents longer than the replacement");
            var content = Enumerable.Repeat((byte)42, length).ToArray();
            var modified = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);

            var written = SafeExtraction.TryWriteAllBytesWithinRoot(boundary, target, content, modified);

            Assert.Multiple(() =>
            {
                Assert.That(written, Is.True);
                Assert.That(File.ReadAllBytes(target), Is.EqualTo(content));
                Assert.That(File.GetLastWriteTimeUtc(target), Is.EqualTo(modified).Within(TimeSpan.FromSeconds(2)));
            });
        }

        [Test]
        public void TryWriteAllBytesWithinRoot_WhenFileIsLocked_PreservesContentsAndReleasesDirectories()
        {
            var boundary = Path.Combine(_tempDirectory, "extract");
            Directory.CreateDirectory(boundary);
            var target = Path.Combine(boundary, "FILE.TXT");
            File.WriteAllText(target, "original");
            using (var busy = File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<Win32Exception>(() => SafeExtraction.TryWriteAllBytesWithinRoot(boundary, target, new byte[] { 1 }, null));

            var moved = Path.Combine(_tempDirectory, "moved");
            Directory.Move(boundary, moved);
            Assert.That(File.ReadAllText(Path.Combine(moved, "FILE.TXT")), Is.EqualTo("original"));
        }

        [Test]
        public void TryAcquireDirectoryChain_WithOutsideTarget_DoesNotCreateIt()
        {
            var boundary = Path.Combine(_tempDirectory, "extract");
            var outside = Path.Combine(_tempDirectory, "outside");

            using var directories = SafeExtraction.TryAcquireDirectoryChain(boundary, outside);

            Assert.Multiple(() =>
            {
                Assert.That(directories, Is.Null);
                Assert.That(Directory.Exists(outside), Is.False);
                Assert.That(Directory.Exists(boundary), Is.False);
            });
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern SafeFileHandle CreateFile(string path, uint access, uint share,
                IntPtr security, uint disposition, uint flags, IntPtr template);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize,
                IntPtr output, int outputSize, out int bytesReturned, IntPtr overlapped);
        }
    }
}
