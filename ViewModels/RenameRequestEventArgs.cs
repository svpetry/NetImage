using NetImage.Models;

namespace NetImage.ViewModels
{
    public class RenameRequestEventArgs : System.EventArgs
    {
        public RenameRequestEventArgs(TreeItem item)
        {
            Item = item;
            NewName = item.Name;
        }

        public TreeItem Item { get; }
        public string NewName { get; set; }
    }
}
