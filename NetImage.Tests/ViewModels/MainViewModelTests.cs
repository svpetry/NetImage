using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using NetImage.Models;
using NetImage.ViewModels;
using NetImage.Views;
using NetImage.Workers;
using NUnit.Framework;

namespace NetImage.Tests.ViewModels
{
    [TestFixture]
    public class MainViewModelTests
    {
        [Test]
        public void ApplicationVersion_UsesConfiguredAssemblyVersion()
        {
            var version = typeof(MainViewModel).Assembly.GetName().Version;
            Assert.That(version, Is.Not.Null);
            Assert.That(version, Is.Not.EqualTo(new Version(0, 0, 0, 0)));

            var viewModel = new MainViewModel();

            Assert.That(viewModel.ApplicationVersion, Is.EqualTo($"{version!.Major}.{version.Minor}"));
        }

        [Test]
        [Apartment(System.Threading.ApartmentState.STA)]
        public void NewDiskImageDialog_CanBeCreated()
        {
            Assert.That(() => new NewDiskImageDialog(), Throws.Nothing);
        }

        [Test]
        public void BuildTreeView_WhenCurrentFolderExists_RestoresThatFolder()
        {
            var worker = CreateWorkerWithNestedFolder();
            var viewModel = CreateViewModel(worker);

            InvokeBuildTreeView(viewModel);

            var currentFolder = FindTreeItem(viewModel.TreeItems, "DOCS\\TEXT");
            Assert.That(currentFolder, Is.Not.Null);

            viewModel.CurrentFolder = currentFolder;
            viewModel.SelectedItem = currentFolder;

            InvokeBuildTreeView(viewModel);

            var restoredFolder = FindTreeItem(viewModel.TreeItems, "DOCS\\TEXT");
            var rootNode = viewModel.TreeItems.Single();

            Assert.Multiple(() =>
            {
                Assert.That(restoredFolder, Is.Not.Null);
                Assert.That(viewModel.CurrentFolder, Is.SameAs(restoredFolder));
                Assert.That(viewModel.SelectedItem, Is.SameAs(restoredFolder));
                Assert.That(restoredFolder!.IsSelected, Is.True);
                Assert.That(rootNode.IsSelected, Is.False);
            });
        }

        [Test]
        public void BuildTreeView_WhenCurrentFolderWasRemoved_RestoresNearestExistingParent()
        {
            var worker = CreateWorkerWithNestedFolder();
            var viewModel = CreateViewModel(worker);

            InvokeBuildTreeView(viewModel);

            var currentFolder = FindTreeItem(viewModel.TreeItems, "DOCS\\TEXT");
            Assert.That(currentFolder, Is.Not.Null);

            viewModel.CurrentFolder = currentFolder;
            viewModel.SelectedItem = currentFolder;

            worker.DeleteEntry("DOCS\\TEXT");

            InvokeBuildTreeView(viewModel);

            var restoredFolder = FindTreeItem(viewModel.TreeItems, "DOCS");

            Assert.Multiple(() =>
            {
                Assert.That(restoredFolder, Is.Not.Null);
                Assert.That(viewModel.CurrentFolder, Is.SameAs(restoredFolder));
                Assert.That(viewModel.SelectedItem, Is.SameAs(restoredFolder));
                Assert.That(restoredFolder!.IsSelected, Is.True);
            });
        }

        private static MainViewModel CreateViewModel(DiskImageWorker worker)
        {
            var viewModel = new MainViewModel();
            var imageWorkerField = typeof(MainViewModel).GetField("_imageWorker", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(imageWorkerField, Is.Not.Null);
            imageWorkerField!.SetValue(viewModel, worker);
            return viewModel;
        }

        private static DiskImageWorker CreateWorkerWithNestedFolder()
        {
            var worker = new DiskImageWorker(string.Empty);
            worker.CreateBlankImage(1_474_560, "DISK");
            worker.CreateFolder(string.Empty, "DOCS");
            worker.CreateFolder("DOCS", "TEXT");
            worker.AddFile("DOCS\\TEXT", "FILE.TXT", Encoding.ASCII.GetBytes("hello"));
            return worker;
        }

        private static void InvokeBuildTreeView(MainViewModel viewModel)
        {
            var buildTreeViewMethod = typeof(MainViewModel).GetMethod("BuildTreeView", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(buildTreeViewMethod, Is.Not.Null);
            buildTreeViewMethod!.Invoke(viewModel, null);
        }

        private static TreeItem? FindTreeItem(IEnumerable<TreeItem> items, string path)
        {
            foreach (var item in items)
            {
                if (item.Path == path)
                    return item;

                var childMatch = FindTreeItem(item.Children, path);
                if (childMatch != null)
                    return childMatch;
            }

            return null;
        }
    }
}
