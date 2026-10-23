using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetImage.Utils
{
    /// <summary>
    /// Extracts through directories held open without write/delete sharing. Each component is opened without
    /// following reparse points and checked before use, so validation remains effective until the write completes.
    /// </summary>
    internal static class SafeExtraction
    {
        private const uint GenericWrite = 0x40000000;
        private const uint FileReadAttributes = 0x80;
        private const uint FileTraverse = 0x20;
        private const uint Synchronize = 0x00100000;
        private const uint ShareRead = 0x01;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;
        private const uint OpenReparsePoint = 0x00200000;

        public static bool TryCreateDirectoryWithinRoot(string rootFull, string fullPath)
        {
            if (!IsContained(rootFull, fullPath, allowRoot: false))
                return false;

            using var directories = TryAcquireDirectoryChain(rootFull, fullPath);
            return directories != null;
        }

        public static bool EnsureExtractionRootWithinBoundary(string boundaryRoot, string rootPath)
        {
            using var directories = TryAcquireDirectoryChain(boundaryRoot, rootPath);
            return directories != null;
        }

        public static bool TryWriteAllBytesWithinRoot(string rootFull, string fullPath, byte[] content, DateTime? lastWriteTime)
        {
            fullPath = Path.GetFullPath(fullPath);
            if (!IsContained(rootFull, fullPath, allowRoot: false))
                return false;

            var parent = Path.GetDirectoryName(fullPath);
            if (parent == null)
                return false;

            // Keep the boundary and every ancestor pinned through the open, write, and timestamp update.
            using var directories = TryAcquireDirectoryChain(rootFull, parent);
            if (directories == null)
                return false;

            using var handle = TryOpenFile(directories, Path.GetFileName(fullPath));
            if (handle == null)
                return false;

            using var stream = new FileStream(handle, FileAccess.Write);
            stream.Write(content);
            stream.SetLength(content.Length);
            stream.Flush();
            if (lastWriteTime.HasValue)
                File.SetLastWriteTime(handle, lastWriteTime.Value);

            return true;
        }

        internal static SafeFileHandle? TryOpenFile(DirectoryLease directories, string fileName)
        {
            var handle = OpenChild(directories.LastHandle, fileName, directory: false);
            if (handle == null)
                return null;
            try
            {
                var info = GetInformation(handle);
                if ((info.FileAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0 &&
                    info.NumberOfLinks <= 1)
                    return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
            handle.Dispose();
            return null;
        }

        // Open a single component relative to a verified directory handle. No path lookup can be redirected through
        // a renamed ancestor, and OBJ_DONT_REPARSE rejects reparsing during this atomic open/create operation.
        private static SafeFileHandle? OpenChild(SafeFileHandle parent, string name, bool directory)
        {
            if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
                return null;

            var text = Marshal.StringToHGlobalUni(name);
            var descriptor = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.UnicodeString>());
            var parentReferenced = false;
            try
            {
                var byteLength = checked((ushort)(name.Length * 2));
                Marshal.StructureToPtr(new NativeMethods.UnicodeString
                {
                    Length = byteLength,
                    MaximumLength = byteLength,
                    Buffer = text
                }, descriptor, false);
                parent.DangerousAddRef(ref parentReferenced);
                var attributes = new NativeMethods.ObjectAttributes
                {
                    Length = Marshal.SizeOf<NativeMethods.ObjectAttributes>(),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = descriptor,
                    Attributes = 0x40 | 0x1000 // OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE
                };
                var access = FileReadAttributes | Synchronize | (directory ? FileTraverse : GenericWrite);
                var options = OpenReparsePoint | 0x20u | (directory ? 0x01u : 0x40u);
                var status = NativeMethods.NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero,
                    directory ? 0x10u : 0x80u, ShareRead, 3, options, IntPtr.Zero, 0); // FILE_OPEN_IF
                if (status >= 0)
                    return handle;

                handle?.Dispose();
                if (unchecked((uint)status) is 0xC000050B or 0xC0000280 or 0xC0000281)
                    // Reparse point encountered, unresolved, or a directory changed into a reparse point.
                    return null;
                throw new Win32Exception((int)NativeMethods.RtlNtStatusToDosError(status), $"Cannot open extraction component '{name}'.");
            }
            finally
            {
                if (parentReferenced)
                    parent.DangerousRelease();
                Marshal.FreeHGlobal(descriptor);
                Marshal.FreeHGlobal(text);
            }
        }

        /// <summary>
        /// Creates missing directories one component at a time while holding each verified parent. Handles are kept
        /// until disposal, preventing ancestors from being renamed/replaced. Attribute-only handles can still change
        /// reparse data, so child creation also atomically rejects reparsing rather than trusting path checks alone.
        /// The walk starts at the volume/share root so the boundary's own ancestors are checked too.
        /// </summary>
        internal static DirectoryLease? TryAcquireDirectoryChain(string boundaryRoot, string targetDirectory)
        {
            boundaryRoot = NormalizeDirectory(boundaryRoot);
            targetDirectory = NormalizeDirectory(targetDirectory);
            if (!IsContained(boundaryRoot, targetDirectory, allowRoot: true))
                return null;

            var directories = new DirectoryLease();
            try
            {
                var current = Path.GetPathRoot(targetDirectory)!;
                if (!directories.TryPin(OpenVolumeRoot(current), current))
                {
                    directories.Dispose();
                    return null;
                }

                foreach (var segment in targetDirectory[current.Length..].Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, segment);
                    var handle = OpenChild(directories.LastHandle, segment, directory: true);
                    if (handle == null || !directories.TryPin(handle, current))
                    {
                        directories.Dispose();
                        return null;
                    }
                }

                return directories;
            }
            catch
            {
                directories.Dispose();
                throw;
            }
        }

        private static string NormalizeDirectory(string path)
            => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        private static bool IsContained(string root, string path, bool allowRoot)
        {
            root = NormalizeDirectory(root);
            path = NormalizeDirectory(path);
            if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase))
                return allowRoot;

            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static void ThrowIfInvalid(SafeFileHandle handle, string path)
        {
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open '{path}' for extraction.");
        }

        private static NativeMethods.FileInformation GetInformation(SafeFileHandle handle)
        {
            if (!NativeMethods.GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot validate an extraction handle.");

            return info;
        }

        internal sealed class DirectoryLease : IDisposable
        {
            private readonly List<SafeFileHandle> _handles = new();
            internal SafeFileHandle LastHandle => _handles[^1];

            internal bool TryPin(SafeFileHandle handle, string path)
            {
                var retained = false;
                try
                {
                    ThrowIfInvalid(handle, path);
                    var attributes = GetInformation(handle).FileAttributes;
                    if ((attributes & FileAttributes.Directory) == 0 ||
                        (attributes & FileAttributes.ReparsePoint) != 0)
                        return false;

                    _handles.Add(handle);
                    retained = true;
                    return true;
                }
                finally
                {
                    if (!retained)
                        handle.Dispose();
                }
            }

            public void Dispose()
            {
                for (var i = _handles.Count - 1; i >= 0; i--)
                    _handles[i].Dispose();
                _handles.Clear();
            }
        }

        private static SafeFileHandle OpenVolumeRoot(string path)
            => NativeMethods.CreateFile(path, FileTraverse | FileReadAttributes, ShareRead, IntPtr.Zero,
                OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);

        private static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential)]
            internal struct FileInformation
            {
                public FileAttributes FileAttributes;
                public uint CreationTimeLow;
                public uint CreationTimeHigh;
                public uint LastAccessTimeLow;
                public uint LastAccessTimeHigh;
                public uint LastWriteTimeLow;
                public uint LastWriteTimeHigh;
                public uint VolumeSerialNumber;
                public uint FileSizeHigh;
                public uint FileSizeLow;
                public uint NumberOfLinks;
                public uint FileIndexHigh;
                public uint FileIndexLow;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode,
                IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

            [StructLayout(LayoutKind.Sequential)]
            internal struct UnicodeString
            {
                public ushort Length;
                public ushort MaximumLength;
                public IntPtr Buffer;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct ObjectAttributes
            {
                public int Length;
                public IntPtr RootDirectory;
                public IntPtr ObjectName;
                public uint Attributes;
                public IntPtr SecurityDescriptor;
                public IntPtr SecurityQualityOfService;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct IoStatusBlock
            {
                public IntPtr Status;
                public UIntPtr Information;
            }

            [DllImport("ntdll.dll")]
            internal static extern int NtCreateFile(out SafeFileHandle handle, uint desiredAccess,
                ref ObjectAttributes attributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize,
                uint fileAttributes, uint shareAccess, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);

            [DllImport("ntdll.dll")]
            internal static extern uint RtlNtStatusToDosError(int status);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
        }
    }
}
