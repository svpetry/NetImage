using System;

namespace NetImage.ViewModels
{
    public class CloseImageRequestEventArgs : EventArgs
    {
        public bool AllowClose { get; set; }
        public bool SaveChanges { get; set; }
    }
}
