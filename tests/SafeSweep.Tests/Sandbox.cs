using System.Diagnostics;
using SafeSweep.Core.Safety;

namespace SafeSweep.Tests;

/// <summary>
/// A throw-away directory tree that mimics a Windows layout (Windows,
/// Program Files, ProgramData, a user profile with AppData and personal
/// folders). Every test that deletes anything does so only inside it.
/// </summary>
public sealed class Sandbox : IDisposable
{
    public Sandbox()
        : this(Path.Combine(Path.GetTempPath(), "SafeSweepTests", Guid.NewGuid().ToString("N")))
    {
    }

    /// <summary>Builds the tree under <paramref name="root"/> (used by the README screenshots).</summary>
    public Sandbox(string root)
    {
        Root = root;
        Windows = Dir("Windows");
        Dir(@"Windows\System32");
        WindowsTemp = Dir(@"Windows\Temp");
        Dir(@"Windows\SoftwareDistribution\Download");
        Dir(@"Windows\SoftwareDistribution\DataStore");
        ProgramFiles = Dir("Program Files");
        ProgramFilesX86 = Dir("Program Files (x86)");
        ProgramData = Dir("ProgramData");
        ProfilesRoot = Dir("Users");
        Profile = Dir(@"Users\me");
        Dir(@"Users\other\Documents");
        LocalAppData = Dir(@"Users\me\AppData\Local");
        RoamingAppData = Dir(@"Users\me\AppData\Roaming");
        LocalLow = Dir(@"Users\me\AppData\LocalLow");
        Temp = Dir(@"Users\me\AppData\Local\Temp");
        Documents = Dir(@"Users\me\Documents");
        Downloads = Dir(@"Users\me\Downloads");
        Desktop = Dir(@"Users\me\Desktop");
        OneDrive = Dir(@"Users\me\OneDrive");
        DataFolder = Dir(@"Users\me\Projects");

        Paths = new SystemPaths
        {
            SystemDrive = Path.GetPathRoot(Root)!,
            WindowsDir = Windows,
            ProgramFiles = ProgramFiles,
            ProgramFilesX86 = ProgramFilesX86,
            ProgramData = ProgramData,
            UserProfile = Profile,
            ProfilesRoot = ProfilesRoot,
            LocalAppData = LocalAppData,
            RoamingAppData = RoamingAppData,
            LocalAppDataLow = LocalLow,
            UserTemp = Temp,
            AppDataRoot = Path.Combine(LocalAppData, "SafeSweep"),
            PersonalFolders = [Documents, Downloads, Desktop, OneDrive],
            Downloads = Downloads,
            DefaultReviewRoots = [Downloads, Documents, Desktop],
            CloudRoots = [OneDrive],
        };
    }

    public string Root { get; }

    public string Windows { get; }

    public string WindowsTemp { get; }

    public string ProgramFiles { get; }

    public string ProgramFilesX86 { get; }

    public string ProgramData { get; }

    public string ProfilesRoot { get; }

    public string Profile { get; }

    public string LocalAppData { get; }

    public string RoamingAppData { get; }

    public string LocalLow { get; }

    public string Temp { get; }

    public string Documents { get; }

    public string Downloads { get; }

    public string Desktop { get; }

    public string OneDrive { get; }

    public string DataFolder { get; }

    public SystemPaths Paths { get; }

    public string Dir(string relative)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string path, string content = "data", int ageDays = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        if (ageDays > 0)
        {
            DateTime old = DateTime.UtcNow.AddDays(-ageDays);
            System.IO.File.SetCreationTimeUtc(path, old);
            System.IO.File.SetLastWriteTimeUtc(path, old);
        }

        return path;
    }

    public static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process p = Process.Start(psi)!;
        p.WaitForExit();
        if (!Directory.Exists(link))
        {
            throw new InvalidOperationException("Junction could not be created: " + p.StandardError.ReadToEnd());
        }
    }

    public void Dispose()
    {
        SafeSweep.Core.Logging.AppLog.Shutdown();
        try
        {
            // Remove junctions first so deleting the tree never follows them.
            foreach (string dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).ToList())
            {
                var info = new DirectoryInfo(dir);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    info.Delete();
                }
            }

            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // Best effort: it lives in %TEMP%.
        }
    }
}
