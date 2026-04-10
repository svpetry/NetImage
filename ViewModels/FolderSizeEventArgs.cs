using System;

namespace NetImage.ViewModels
{
    public class FolderSizeEventArgs : EventArgs
    {
        public string FolderName { get; }
        public long TotalSize { get; }
        public int FileCount { get; }
        public int FolderCount { get; }

        public FolderSizeEventArgs(string folderName, long totalSize, int fileCount, int folderCount)
        {
            FolderName = folderName;
            TotalSize = totalSize;
            FileCount = fileCount;
            FolderCount = folderCount;
        }
    }
}
