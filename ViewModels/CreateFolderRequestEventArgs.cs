using System;

namespace NetImage.ViewModels
{
    public class CreateFolderRequestEventArgs : EventArgs
    {
        public string? FolderName { get; set; }
    }
}
