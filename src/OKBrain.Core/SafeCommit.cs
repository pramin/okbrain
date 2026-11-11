using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OKBrain.Core;

public static partial class BrainWrites
{
    private static bool UseNativeCommit => OperatingSystem.IsLinux() && !ForcePortableCommit;

    private static void CommitFile(string root, SafeFileHandle? rootFd, string path, string? expected, string? text)
    {
        if (rootFd == null) { PortableCommit(root, path, expected, text); return; }
        SafeFileHandle parent = rootFd;
        var opened = new List<SafeFileHandle>();
        string? temp = null;
        try
        {
            var parts = path.Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                int fd = openat(parent.DangerousGetHandle().ToInt32(), part, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, 0);
                if (fd < 0 && Marshal.GetLastPInvokeError() == 2 && text != null)
                {
                    VerifyDirectory(root, string.Join('/', parts[..(i + 1)]), parent.DangerousGetHandle().ToInt32());
                    if (mkdirat(parent.DangerousGetHandle().ToInt32(), part, Convert.ToUInt32("755", 8)) != 0 && Marshal.GetLastPInvokeError() != 17) Fail("create directory");
                    fd = openat(parent.DangerousGetHandle().ToInt32(), part, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, 0);
                }
                if (fd < 0) Fail("open destination directory");
                parent = new SafeFileHandle((IntPtr)fd, true);
                opened.Add(parent);
            }
            int dir = parent.DangerousGetHandle().ToInt32();
            var name = parts[^1];
            if (text != null)
            {
                temp = ".okbrain-" + Guid.NewGuid().ToString("N") + ".tmp";
                int fd = openat(dir, temp, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, Convert.ToUInt32("666", 8));
                if (fd < 0) Fail("create temporary file");
                using var stream = new FileStream(new SafeFileHandle((IntPtr)fd, true), FileAccess.Write);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(text);
            }
            // The directory can be renamed by another process even while held open.
            // Verify its current location immediately before checking and replacing the target.
            VerifyDirectory(root, path, dir);
            int targetFd = openat(dir, name, O_RDONLY | O_NOFOLLOW | O_CLOEXEC, 0);
            if (targetFd < 0)
            {
                if (Marshal.GetLastPInvokeError() != 2 || expected != null)
                    throw new IOException($"Bundle changed concurrently at '{path}'.");
            }
            else
            {
                using var stream = new FileStream(new SafeFileHandle((IntPtr)targetFd, true), FileAccess.Read);
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                if (expected == null || Encoding.UTF8.GetString(bytes.ToArray()) != expected)
                    throw new IOException($"Bundle changed concurrently at '{path}'.");
            }
            // POSIX has no compare-and-swap rename by file contents: a non-cooperating
            // process can still change the target or move its parent after this check.
            int result = text == null ? unlinkat(dir, name, 0) :
                expected == null ? renameat2(dir, temp!, dir, name, 1) : renameat(dir, temp!, dir, name);
            if (result != 0) Fail("replace destination");
            temp = null;
        }
        finally
        {
            if (temp != null) unlinkat(parent.DangerousGetHandle().ToInt32(), temp, 0);
            foreach (var handle in opened) handle.Dispose();
        }
    }

    private static string HandlePath(SafeFileHandle handle) =>
        new DirectoryInfo($"/proc/self/fd/{handle.DangerousGetHandle().ToInt32()}").ResolveLinkTarget(true)?.FullName
        ?? throw new IOException("Cannot resolve bundle directory handle.");

    private static void VerifyDirectory(string root, string path, int dir)
    {
        var resolved = new DirectoryInfo($"/proc/self/fd/{dir}").ResolveLinkTarget(true)?.FullName;
        var expected = Path.GetDirectoryName(Path.Combine(root, path));
        if (resolved == null || !string.Equals(resolved, expected, StringComparison.Ordinal))
            throw new IOException($"Destination parent changed or escapes the bundle: '{path}'.");
    }

    private static void ChangeDirectoryNative(string root, SafeFileHandle rootFd, string path, bool create)
    {
        var parts = path.Split('/');
        SafeFileHandle parent = rootFd;
        var opened = new List<SafeFileHandle>();
        try
        {
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                int dir = parent.DangerousGetHandle().ToInt32();
                int fd = openat(dir, part, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, 0);
                if (fd < 0 && Marshal.GetLastPInvokeError() == 2 && create)
                {
                    VerifyDirectory(root, string.Join('/', parts[..(i + 1)]), dir);
                    if (mkdirat(dir, part, Convert.ToUInt32("755", 8)) != 0) Fail("create directory");
                    fd = openat(dir, part, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, 0);
                }
                if (fd < 0) Fail("open destination directory");
                parent = new SafeFileHandle((IntPtr)fd, true);
                opened.Add(parent);
            }
            BeforeDirectoryChange?.Invoke(path);
            int parentFd = parent.DangerousGetHandle().ToInt32();
            VerifyDirectory(root, path, parentFd);
            if ((create ? mkdirat(parentFd, parts[^1], Convert.ToUInt32("755", 8)) : unlinkat(parentFd, parts[^1], 0x200)) != 0)
                Fail(create ? "create directory" : "remove directory");
        }
        finally { foreach (var handle in opened) handle.Dispose(); }
    }

    // Path-based fallback cannot prevent a component being swapped after verification.
    private static void VerifyPortablePath(string root, string path, bool createParents)
    {
        var fullRoot = Path.GetFullPath(root);
        var rootPart = Path.GetPathRoot(fullRoot)!;
        var current = rootPart;
        foreach (var part in fullRoot[rootPart.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links are not allowed in write paths.");
        }
        var parts = path.Split('/');
        foreach (var part in parts[..^1])
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current) && createParents) Directory.CreateDirectory(current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links are not allowed in write paths.");
        }
        if (!BundleReader.IsWithin(fullRoot, current)) throw new IOException("Destination escapes the bundle.");
        var target = Path.Combine(current, parts[^1]);
        if ((File.Exists(target) || Directory.Exists(target)) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Symbolic links are not allowed in write paths.");
    }

    private static void PortableCommit(string root, string path, string? expected, string? text)
    {
        VerifyPortablePath(root, path, text != null);
        var target = Path.Combine(root, path);
        string? temp = null;
        try
        {
            if (text != null)
            {
                temp = Path.Combine(Path.GetDirectoryName(target)!, ".okbrain-" + Guid.NewGuid().ToString("N") + ".tmp");
                using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(text);
            }
            VerifyPortablePath(root, path, false);
            if (File.Exists(target) ? expected == null || Encoding.UTF8.GetString(File.ReadAllBytes(target)) != expected : expected != null || Directory.Exists(target))
                throw new IOException($"Bundle changed concurrently at '{path}'.");
            if (text == null) File.Delete(target);
            else File.Move(temp!, target, expected != null);
            temp = null;
        }
        finally { if (temp != null) File.Delete(temp); }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        int fd = openat(-100, path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, 0);
        if (fd < 0) Fail("open bundle directory");
        return new SafeFileHandle((IntPtr)fd, true);
    }
    private static void Fail(string action) => throw new IOException($"Could not {action}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    private const int O_RDONLY = 0, O_WRONLY = 1, O_CREAT = 64, O_EXCL = 128, O_DIRECTORY = 65536, O_NOFOLLOW = 131072, O_CLOEXEC = 524288;
    [DllImport("libc", SetLastError = true)] private static extern int openat(int dirfd, string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int mkdirat(int dirfd, string path, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int renameat(int olddirfd, string oldpath, int newdirfd, string newpath);
    [DllImport("libc", SetLastError = true)] private static extern int renameat2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags);
    [DllImport("libc", SetLastError = true)] private static extern int unlinkat(int dirfd, string path, int flags);
}
