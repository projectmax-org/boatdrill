using Boatdrill.Core.Text;

namespace Boatdrill.Core.Drill;

/// <summary>
/// The scratch folder of boatdrill-cli, where its drills, scans and rehearsals work. Windows gives each user a temp
/// folder of their own, so there it is %TEMP%\boatdrill, as it always was. On Linux and macOS /tmp is every user's:
/// another one could make the folder first and own the place a drill restores into, or read the copies restored
/// there. So there it is boatdrill in the user's cache folder, made for them alone, and a scratch folder that is a
/// link, another user's or open to other users is refused rather than worked in.
/// </summary>
public static class RunnerScratch
{
    /// <summary>The folder made inside the one --scratch names, so that nothing else of that folder is ever touched.</summary>
    private const string FolderName = "Boatdrill scratch";

    /// <summary>Any of these lets users other than the owner list, enter or write in a folder.</summary>
    private const UnixFileMode OpenToOthers = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                              UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// The scratch folder: a "Boatdrill scratch" folder in the one --scratch names, or else %TEMP%\boatdrill on Windows
    /// and boatdrill in the user's cache folder on Linux and macOS. On Windows nothing is made here: a drill, scan or
    /// rehearsal makes the folder when it starts, as it always has. Elsewhere the folder is made for the user alone, or
    /// the one there is checked to be theirs alone, and an <see cref="ArgumentException"/> says why when it is not.
    /// </summary>
    public static string Prepare(string? scratch)
    {
        if (OperatingSystem.IsWindows()) return scratch is null ? Path.Combine(Path.GetTempPath(), "boatdrill") : Path.Combine(scratch, FolderName);

        return EnsurePrivate(Folder(scratch, Environment.GetEnvironmentVariable("XDG_CACHE_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    /// <summary>
    /// Where the folder goes on Linux and macOS: in the folder --scratch names, else in $XDG_CACHE_HOME, or in ~/.cache
    /// when that is not set to an absolute path, as the XDG base directory specification has it.
    /// </summary>
    internal static string Folder(string? scratch, string? cacheHome, string home)
    {
        if (scratch is not null) return Path.Combine(scratch, FolderName);
        if (cacheHome is { Length: > 0 } && Path.IsPathFullyQualified(cacheHome)) return Path.Combine(cacheHome, "boatdrill");
        if (Path.IsPathFullyQualified(home)) return Path.Combine(home, ".cache", "boatdrill");

        // Without a home folder, ~/.cache would be a .cache folder in whatever folder the runner was started from.
        throw new ArgumentException(Language.Pick(
            "Boatdrill cannot tell where your home folder is, to keep its scratch folder in it. Set HOME, or name a folder with --scratch.",
            "작업 폴더를 둘 홈 폴더를 알 수 없습니다. HOME 환경 변수를 설정하거나 --scratch로 폴더를 지정하세요."));
    }

    /// <summary>
    /// On Linux and macOS, makes the folder for the user alone (rwx------), or makes sure that the one there is theirs
    /// alone, and returns it; throws an <see cref="ArgumentException"/> that says why when it is not. On Windows the
    /// folder is left as it is.
    /// </summary>
    internal static string EnsurePrivate(string folder)
    {
        if (OperatingSystem.IsWindows()) return folder;

        var full = Path.GetFullPath(folder);

        // Nothing is made through a link, which would make the folder wherever the link leads. One that leads nowhere
        // would only make the making fail, with an error that says less.
        if (new DirectoryInfo(full).LinkTarget is null) Directory.CreateDirectory(full, Scratch.UserOnly);

        // What is there is checked even when it was made just now: another user may have made it, or a link, meanwhile.
        var there = new DirectoryInfo(full);
        if (there.LinkTarget is not null)
        {
            throw new ArgumentException(Language.Pick(
                $"The scratch folder {full} is a symbolic link, so what Boatdrill restores there would land wherever it leads. " +
                "Delete the link, or name another folder with --scratch.",
                $"작업 폴더({full})는 심볼릭 링크라서, Boatdrill이 복원하는 파일이 링크가 가리키는 곳에 놓이게 됩니다. " +
                "링크를 지우거나 --scratch로 다른 폴더를 지정하세요."));
        }

        // .NET cannot say who owns a folder, but only its owner may change its mode: setting the one it has, which
        // changes nothing, fails for anyone else. Root may change any folder's mode, so for root this proves nothing.
        var mode = there.UnixFileMode;
        try
        {
            there.UnixFileMode = mode;
        }
        catch (UnauthorizedAccessException)
        {
            throw new ArgumentException(Language.Pick(
                $"The scratch folder {full} belongs to another user, who could read what Boatdrill restores there. " +
                "Name a folder of your own with --scratch.",
                $"작업 폴더({full})는 다른 사용자의 폴더라서, Boatdrill이 그곳에 복원하는 파일을 그 사용자가 읽을 수 있습니다. " +
                "--scratch로 본인의 폴더를 지정하세요."));
        }

        if ((mode & OpenToOthers) != 0)
        {
            throw new ArgumentException(Language.Pick(
                $"Other users can open the scratch folder {full}, and so read what Boatdrill restores there. " +
                "Make it yours alone with chmod 700, delete it, or name another folder with --scratch.",
                $"작업 폴더({full})는 다른 사용자도 열 수 있어서, Boatdrill이 그곳에 복원하는 파일도 읽을 수 있습니다. " +
                "chmod 700으로 본인만 열 수 있게 하거나, 폴더를 지우거나, --scratch로 다른 폴더를 지정하세요."));
        }

        return folder;
    }
}
