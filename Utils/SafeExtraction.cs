using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetImage.Utils
{
    /// <summary>
    /// Filesystem helpers that write extracted image content into a host directory while guaranteeing that no write
    /// can land outside the chosen destination root, even when the destination already contains directory junctions,
    /// symbolic links or other reparse points. Textual containment alone (see
    /// <see cref="NetImage.Workers.DiskImageWorker.ResolveSafeDestinationPath"/>) is not enough because Windows will
    /// transparently follow those reparse points, so every existing path component below the root is inspected and the
    /// final file is opened without following reparse points.
    /// </summary>
    internal static class SafeExtraction
    {
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_ALL = 0x00000001 | 0x00000002 | 0x00000004; // READ | WRITE | DELETE
        private const uint CREATE_ALWAYS = 2;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

        /// <summary>
        /// Creates <paramref name="fullPath"/> (and any missing parents) as real directories. Returns false, without
        /// touching the filesystem outside the already-created safe prefix, when the target or an existing ancestor at
        /// or below <paramref name="rootFull"/> is a reparse point, so extraction never writes through a junction.
        /// </summary>
        public static bool TryCreateDirectoryWithinRoot(string rootFull, string fullPath)
        {
            if (!IsTextuallyContained(rootFull, fullPath))
                return false;

            return EnsureDirectoryChain(rootFull, fullPath);
        }

        /// <summary>
        /// Writes <paramref name="content"/> to <paramref name="fullPath"/>, first making sure every ancestor at or
        /// below <paramref name="rootFull"/> is a real (non-reparse) directory and that the destination file itself is
        /// not a redirected reparse target. The final file is opened with FILE_FLAG_OPEN_REPARSE_POINT so a reparse
        /// point substituted after validation is opened as the link object instead of being followed outside the root;
        /// such a target is reported as unsafe (returns false) and left untouched.
        /// </summary>
        public static bool TryWriteAllBytesWithinRoot(string rootFull, string fullPath, byte[] content, DateTime? lastWriteTime)
        {
            if (!IsTextuallyContained(rootFull, fullPath))
                return false;

            var parent = Path.GetDirectoryName(fullPath);
            if (parent == null || !EnsureDirectoryChain(rootFull, parent))
                return false;

            // A pre-existing final file that is itself a symlink/junction must not be written through. GetFileAttributesEx
            // does not traverse reparse points, so this reports the link itself rather than its target.
            if (File.Exists(fullPath) && IsReparsePoint(fullPath))
                return false;

            using var handle = CreateFileNoFollow(fullPath, GENERIC_WRITE, FILE_SHARE_ALL, CREATE_ALWAYS);
            if (handle == null)
                return false; // leaf turned out to be a reparse point: refuse to follow it.

            using (var stream = new FileStream(handle, FileAccess.Write))
            {
                stream.Write(content, 0, content.Length);
                stream.SetLength(content.Length);
            }

            if (lastWriteTime.HasValue)
                File.SetLastWriteTime(fullPath, lastWriteTime.Value);

            return true;
        }

        /// <summary>
        /// Walks every path component between <paramref name="rootFull"/> (exclusive) and <paramref name="targetDir"/>
        /// (inclusive), rejecting any existing reparse point and creating missing components as real directories.
        /// </summary>
        private static bool EnsureDirectoryChain(string rootFull, string targetDir)
        {
            var rootPrefix = EnsureTrailingSeparator(rootFull);

            // A file directly under the root has the root itself as its parent: nothing to create, root is trusted.
            if (string.Equals(targetDir, rootFull, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!targetDir.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var relative = targetDir.Substring(rootPrefix.Length);
            var current = rootFull;

            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);

                if (Directory.Exists(current))
                {
                    if (IsReparsePoint(current))
                        return false;

                    continue;
                }

                Directory.CreateDirectory(current);

                // A component created moments ago must still be a plain directory before anything writes below it.
                if (IsReparsePoint(current))
                    return false;
            }

            return true;
        }

        private static bool IsTextuallyContained(string rootFull, string fullPath)
        {
            var rootPrefix = EnsureTrailingSeparator(rootFull);
            return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(fullPath, rootFull, StringComparison.OrdinalIgnoreCase);
        }

        private static string EnsureTrailingSeparator(string path)
            => path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;

        private static bool IsReparsePoint(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Opens <paramref name="path"/> with FILE_FLAG_OPEN_REPARSE_POINT so the final component is never traversed.
        /// Returns null when the path is a reparse point (so the caller refuses to follow it) and throws for genuine IO
        /// failures. A non-existent path is created as a plain file.
        /// </summary>
        private static SafeFileHandle? CreateFileNoFollow(string path, uint desiredAccess, uint shareMode, uint creationDisposition)
        {
            var handle = NativeMethods.CreateFile(
                path, desiredAccess, shareMode, IntPtr.Zero,
                creationDisposition, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                if (IsHandleReparsePoint(handle))
                {
                    handle.Dispose();
                    return null;
                }

                return handle;
            }

            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Failed to open '{path}' for extraction.");
        }

        private static bool IsHandleReparsePoint(SafeFileHandle handle)
        {
            if (!NativeMethods.GetFileInformationByHandle(handle, out var info))
                return false;

            return (info.dwFileAttributes & 0x00000400 /* FILE_ATTRIBUTE_REPARSE_POINT */) != 0;
        }

        private static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct BY_HANDLE_FILE_INFORMATION
            {
                public uint dwFileAttributes;
                public uint ftCreationTimeLow;
                public uint ftCreationTimeHigh;
                public uint ftLastAccessTimeLow;
                public uint ftLastAccessTimeHigh;
                public uint ftLastWriteTimeLow;
                public uint ftLastWriteTimeHigh;
                public uint dwVolumeSerialNumber;
                public uint nFileSizeHigh;
                public uint nFileSizeLow;
                public uint nNumberOfLinks;
                public uint nFileIndexHigh;
                public uint nFileIndexLow;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern SafeFileHandle CreateFile(
                string lpFileName,
                uint dwDesiredAccess,
                uint dwShareMode,
                IntPtr lpSecurityAttributes,
                uint dwCreationDisposition,
                uint dwFlagsAndAttributes,
                IntPtr hTemplateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);
        }
    }
}
