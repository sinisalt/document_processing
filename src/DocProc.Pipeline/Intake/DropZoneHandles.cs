using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DocProc.Pipeline.Intake;

/// <summary>What a handle-based step below a tenant's root found.</summary>
internal enum DropZoneOutcome
{
    /// <summary>Done.</summary>
    Done,

    /// <summary>A folder or the file on the way is a link (or not a folder where one is expected); nothing was done.</summary>
    Link,

    /// <summary>The file or a folder on the way does not exist (another host moved it first).</summary>
    Missing,
}

/// <summary>
/// The folder watcher's file system steps bound to handles (Linux on x64 and arm64, where the worker runs): every folder
/// from the tenant's root down is opened relative to the folder above it with <c>O_NOFOLLOW</c>, so a folder or file
/// swapped for a link at any moment is refused by the kernel instead of followed, and nothing is checked first and acted
/// on later. A file is opened the same way with <c>O_NONBLOCK</c>, so a named pipe (FIFO) opens at once instead of
/// waiting for a writer; a move is <c>renameat2</c> between two such folder handles (<c>RENAME_NOREPLACE</c>), and the
/// target folders are made with <c>mkdirat</c> below the channel's handle. The tenant's root itself is the platform's
/// (it may be reached through a mounted volume's link) and is opened as it is.
/// </summary>
internal static partial class DropZoneHandles
{
    private const int AtFdCwd = -100;
    private const int ORdOnly = 0;
    private const int ENoEnt = 2;
    private const int EExist = 17;
    private const int ENotDir = 20;
    private const int EInval = 22;
    private const int ENoSys = 38;
    private const int ELoop = 40;
    private const uint RenameNoReplace = 1;
    private const uint FolderMode = 0x1FF; // 0777, less the process umask

    /// <summary>Whether this platform runs the handle-based steps; elsewhere the watcher checks paths (a narrower window, not none).</summary>
    public static bool Supported => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    // The open(2) flags differ between the two architectures (asm-generic for arm64, x86 for x64).
    private static int ODirectory => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;

    private static int ONoFollow => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;

    private const int ONonBlock = 0x800;
    private const int OCloExec = 0x80000;

    /// <summary>
    /// Opens the file <paramref name="relativePath"/> below <paramref name="root"/> for reading, every part of the way
    /// without following a link. A FIFO or other non-regular file opens without blocking; the caller refuses a handle
    /// it cannot seek.
    /// </summary>
    public static (SafeFileHandle? Handle, DropZoneOutcome Outcome) OpenFile(string root, string relativePath)
    {
        var (folders, name) = Split(relativePath);
        if (name is null)
        {
            return (null, DropZoneOutcome.Missing);
        }

        var (folder, outcome) = OpenFolder(root, folders, create: false);
        if (folder is null)
        {
            return (null, outcome);
        }

        using (folder)
        {
            var fd = OpenAt(folder.Fd, name, ORdOnly | ONoFollow | ONonBlock | OCloExec);
            if (fd < 0)
            {
                return (null, Classify(Marshal.GetLastPInvokeError(), name));
            }

            return (new SafeFileHandle((IntPtr)fd, ownsHandle: true), DropZoneOutcome.Done);
        }
    }

    /// <summary>
    /// Moves the file <paramref name="fromRelative"/> to <paramref name="toRelative"/>, both below <paramref name="root"/>:
    /// the source folder and the target folder are opened without following a link (the target's folders below
    /// <paramref name="createBelow"/> parts are made as needed), and the file is renamed between the two handles. A taken
    /// name gets <paramref name="alternative"/> instead. Returns the target's relative path when moved.
    /// </summary>
    public static (DropZoneOutcome Outcome, string? MovedTo) Move(string root, string fromRelative, string toRelative, int createBelow, Func<string, string> alternative)
    {
        var (fromFolders, fromName) = Split(fromRelative);
        var (toFolders, toName) = Split(toRelative);
        if (fromName is null || toName is null)
        {
            return (DropZoneOutcome.Missing, null);
        }

        var (source, sourceOutcome) = OpenFolder(root, fromFolders, create: false);
        if (source is null)
        {
            return (sourceOutcome, null);
        }

        using (source)
        {
            var (target, targetOutcome) = OpenFolder(root, toFolders, create: true, createBelow);
            if (target is null)
            {
                return (targetOutcome, null);
            }

            using (target)
            {
                foreach (var name in new[] { toName, alternative(toName) })
                {
                    var renamed = RenameAt2(source.Fd, fromName, target.Fd, name, RenameNoReplace);
                    var error = renamed == 0 ? 0 : Marshal.GetLastPInvokeError();
                    if (error is ENoSys or EInval)
                    {
                        // A file system without RENAME_NOREPLACE: the name is looked up first (a race inside the
                        // tenant's own target folder at worst replaces one of its own processed files).
                        if (Exists(target.Fd, name))
                        {
                            continue;
                        }

                        renamed = RenameAt(source.Fd, fromName, target.Fd, name);
                        error = renamed == 0 ? 0 : Marshal.GetLastPInvokeError();
                    }

                    if (error == 0)
                    {
                        return (DropZoneOutcome.Done, string.Join('/', toFolders.Append(name)));
                    }

                    if (error == ENoEnt)
                    {
                        return (DropZoneOutcome.Missing, null);
                    }

                    if (error is ELoop or ENotDir)
                    {
                        return (DropZoneOutcome.Link, null);
                    }

                    if (error != EExist)
                    {
                        throw new IOException($"The file could not be moved (errno {error}).");
                    }
                }

                throw new IOException("The file could not be moved: the target name and its alternative are taken.");
            }
        }
    }

    private static bool Exists(int folder, string name)
    {
        var fd = OpenAt(folder, name, ORdOnly | ONoFollow | ONonBlock | OCloExec);
        if (fd >= 0)
        {
            _ = Close(fd);
            return true;
        }

        return Marshal.GetLastPInvokeError() != ENoEnt;
    }

    /// <summary>The folder <paramref name="folders"/> below <paramref name="root"/>, each part opened without following a link; with <paramref name="create"/>, the parts past the first <paramref name="createBelow"/> are made when missing.</summary>
    private static (FolderHandle? Folder, DropZoneOutcome Outcome) OpenFolder(string root, IReadOnlyList<string> folders, bool create, int createBelow = 0)
    {
        var fd = OpenAt(AtFdCwd, root, ORdOnly | ODirectory | OCloExec);
        if (fd < 0)
        {
            return (null, DropZoneOutcome.Missing);
        }

        var current = new FolderHandle(fd);
        for (var i = 0; i < folders.Count; i++)
        {
            var part = folders[i];
            if (create && i >= createBelow && MkdirAt(current.Fd, part, FolderMode) != 0)
            {
                var mkdirError = Marshal.GetLastPInvokeError();
                if (mkdirError != EExist)
                {
                    current.Dispose();
                    return mkdirError == ENoEnt ? (null, DropZoneOutcome.Missing) : throw new IOException($"{part} could not be made (errno {mkdirError}).");
                }
            }

            var next = OpenAt(current.Fd, part, ORdOnly | ODirectory | ONoFollow | OCloExec);
            var error = next < 0 ? Marshal.GetLastPInvokeError() : 0;
            current.Dispose();
            if (next < 0)
            {
                return (null, Classify(error, part));
            }

            current = new FolderHandle(next);
        }

        return (current, DropZoneOutcome.Done);
    }

    private static DropZoneOutcome Classify(int error, string part) => error switch
    {
        ELoop or ENotDir => DropZoneOutcome.Link,
        ENoEnt => DropZoneOutcome.Missing,
        _ => throw new IOException($"{part} could not be opened (errno {error})."),
    };

    /// <summary>The folders and the file name of a relative path; a part that is empty, <c>.</c> or <c>..</c> is refused.</summary>
    private static (IReadOnlyList<string> Folders, string? Name) Split(string relativePath)
    {
        var parts = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".."))
        {
            return ([], null);
        }

        return (parts[..^1], parts[^1]);
    }

    /// <summary>A folder's descriptor, closed when disposed.</summary>
    private sealed class FolderHandle(int fd) : IDisposable
    {
        public int Fd { get; } = fd;

        public void Dispose() => _ = Close(Fd);
    }

    // "libc" is mapped by the runtime to the C library of the platform (libc.so.6 on glibc).
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(int dirFd, string path, int flags);

    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MkdirAt(int dirFd, string path, uint mode);

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt2(int oldDirFd, string oldPath, int newDirFd, string newPath, uint flags);

    [LibraryImport("libc", EntryPoint = "renameat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt(int oldDirFd, string oldPath, int newDirFd, string newPath);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}
