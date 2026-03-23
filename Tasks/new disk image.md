# New Disk Image Feature Task List

- [ ] Create New Disk Image UI
  - [ ] Add "New" menu item to MainView.xaml
  - [ ] Create a New Disk Image Dialog (Window) with standard format radio buttons (160 KB to 2.88 MB)
  - [ ] Add `NewCommand` to `MainViewModel.cs`
- [ ] Implement "New" disk image generation
  - [ ] Add method to generate a blank FAT disk image
  - [ ] Handle new disk image state in `DiskImageWorker` or `MainViewModel`
- [ ] Handle Save / Save As state
  - [ ] Verify "Save" and "Save As" menu items exist or create them
  - [ ] Ensure "Save" is disabled when a *new* image is open, and "Save As" is enabled
- [ ] Update documentation / tests if required
