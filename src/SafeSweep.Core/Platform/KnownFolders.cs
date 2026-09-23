using System.Runtime.InteropServices;

namespace SafeSweep.Core.Platform;

/// <summary>
/// Resolves Windows known folders through the shell rather than assuming they
/// sit under %USERPROFILE%. With OneDrive Known Folder Move, Desktop/Documents/
/// Pictures really live under the OneDrive folder, and a hard-coded path would
/// protect an empty stub while the real folder sat unguarded.
/// </summary>
public static class KnownFolders
{
    public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid SavedGames = new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4");
    public static readonly Guid OneDrive = new("A52BBA46-E9E1-435F-B3D9-28DAA648C0F6");
    public static readonly Guid LocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");
    public static readonly Guid Contacts = new("56784854-C6CB-462B-8169-88E350ACB882");
    public static readonly Guid Objects3D = new("31C0DD25-9439-4F12-BF41-7FF4EDA38722");
    public static readonly Guid Links = new("BFB9D5E0-C6A9-404C-B2B2-AE6DB6AF4968");
    public static readonly Guid Searches = new("7D1D3A04-DEBB-4115-95CF-2F29DA2920DA");

    public static string? Get(Guid folderId)
    {
        try
        {
            Guid id = folderId;
            int hr = NativeMethods.SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr pointer);
            if (hr != 0 || pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? Get(Environment.SpecialFolder folder)
    {
        string value = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The user's personal content folders: never cleaned automatically.</summary>
    public static IReadOnlyList<string> PersonalContentFolders()
    {
        var list = new List<string?>
        {
            Get(Environment.SpecialFolder.Desktop),
            Get(Environment.SpecialFolder.MyDocuments),
            Get(Environment.SpecialFolder.MyPictures),
            Get(Environment.SpecialFolder.MyMusic),
            Get(Environment.SpecialFolder.MyVideos),
            Get(Environment.SpecialFolder.Favorites),
            Get(Downloads),
            Get(SavedGames),
            Get(Contacts),
            Get(Objects3D),
            Get(Links),
            Get(Searches),
        };

        string? profile = Get(Environment.SpecialFolder.UserProfile);
        if (profile is not null)
        {
            // Belt and braces: the unredirected locations too.
            foreach (string name in new[] { "Desktop", "Documents", "Pictures", "Music", "Videos", "Downloads", "Saved Games", "Favorites", "Contacts", "OneDrive" })
            {
                list.Add(Path.Combine(profile, name));
            }
        }

        foreach (string env in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            list.Add(Environment.GetEnvironmentVariable(env));
        }

        list.Add(Get(OneDrive));

        return list
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Default roots for user-review tools (large files, duplicates, old files).</summary>
    public static IReadOnlyList<string> DefaultReviewRoots()
    {
        var list = new List<string?>
        {
            Get(Downloads),
            Get(Environment.SpecialFolder.MyDocuments),
            Get(Environment.SpecialFolder.Desktop),
            Get(Environment.SpecialFolder.MyVideos),
            Get(Environment.SpecialFolder.MyPictures),
            Get(Environment.SpecialFolder.MyMusic),
        };

        return list
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
