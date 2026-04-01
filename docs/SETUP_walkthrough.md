# Implementing MSI builds for NetImage

I've successfully implemented a streamlined, automated CD pipeline to package your application into a `.msi` standalone installer. This ensures your users get a smooth Windows installing experience right from your GitHub Releases page!

Here is what was accomplished based on the approved implementation plan:

## 1. Single File Compilation
I configured `NetImage.csproj` to compile targeting a true "Single File" portable app by defining `<PublishSingleFile>true</PublishSingleFile>`.
This means when distributing, instead of dozens of .NET system `dll` items littering `Program Files`, WiX will capture only one ultra-neat executable file (`NetImage.exe`).

## 2. WiX v4 Setup Project
I've added the new `NetImage.Setup` project using the modern **WiX Toolset v4**. WiX v4 introduces major architectural changes from older versions: it ships simply via NuGet inside `dotnet`, needing absolutely no manual installation.  
- Included `WixToolset.Sdk` as the project's MSBuild SDK.
- Authored `Package.wxs` to wrap the `NetImage.exe` executable inside.
- Set up a Start Menu shortcut using `diskette.ico`.
- Added the `NetImage.Setup` project to `NetImage.slnx`, so when you open the solution via Visual Studio or Rider, the Setup project natively appears!

## 3. GitHub Actions Continuous Deployment 
To achieve your goal of automatically attaching the `.msi` file when making a release on GitHub, I have designed `.github/workflows/release.yml`.

Whenever you push to GitHub pushing a semantic tag (e.g., executing `git tag v1.4` followed by `git push origin --tags`), the GitHub Action automatically starts. 
- It sets up .NET 10.
- Standardly builds and publishes your application in Single File format.
- Packages it using the MSBuild WiX compilation logic (using `dotnet build`!).
- Takes the newly created `NetImage-v1.4.msi` and seamlessly creates a new GitHub Release page attaching the installer automatically.

## Validation 
We ran a test build on the command line doing:
```powershell
dotnet publish NetImage.csproj -c Release -p:PublishSingleFile=true -o publish_dir
dotnet build NetImage.Setup/NetImage.Setup.wixproj -c Release
```
This correctly validated without any MSI linkage errors! The installer `NetImage.msi` successfully compiled and packaged into the release channel folder. 

When you feel comfortable, simply push your changes and push a `v-` tag, and GitHub Actions will do the rest of the magic.
