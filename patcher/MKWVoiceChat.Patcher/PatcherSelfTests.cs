using System.IO.Compression;
using System.Text.Json;

namespace MKWVoiceChat.Patcher;

internal static class PatcherSelfTests
{
    public static void RunRenamedInstallerSourceAcceptance()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "mkwvc-renamed-installer-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var renamed = Path.Combine(root, "WiiCompiled-VoiceChat-Installer (1).exe");
            File.WriteAllBytes(renamed, [0x4d, 0x5a, 0x00, 0x00]);

            var resolved = UserEntryPointInstaller.ValidateInstallerSourcePath(renamed);
            if (!Path.GetFullPath(resolved).Equals(
                    Path.GetFullPath(renamed),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("Renamed installer source path was not accepted.");
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    public static void RunRetroRewindArchiveCompatibility()
    {
        var root = Path.Combine(Path.GetTempPath(), "mkwvc-rr-archive-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var version = System.Text.Encoding.UTF8.GetBytes("6.13.0");
            var payload = BuildRetroArchive(
                ("RetroRewind6/version.txt", version),
                ("6.13.0.zip", [0, 1, 2]));
            using (var buffer = new MemoryStream(payload))
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Read))
                RetroRewindUpdater.ExtractArchiveEntries(archive, root, new Version(6, 13, 0));

            if (File.ReadAllText(Path.Combine(root, "RetroRewind6", "version.txt")) != "6.13.0" ||
                File.Exists(Path.Combine(root, "6.13.0.zip")))
                throw new InvalidDataException("Retro Rewind package artifact was not ignored.");

            var obsolete = Path.Combine(root, "RetroRewind6", "obsolete.txt");
            File.WriteAllText(obsolete, "old");
            RetroRewindUpdater.ApplyDeletions(root,
            [
                new RetroDeletion(new Version(6, 13, 0), "6.13.0.zip"),
                new RetroDeletion(new Version(6, 13, 0), "/6.13.0.zip"),
                new RetroDeletion(new Version(6, 13, 0), "\\6.13.0.zip"),
                new RetroDeletion(new Version(6, 13, 1), "RetroRewind6/obsolete.txt")
            ]);
            if (File.Exists(obsolete))
                throw new InvalidDataException("Regular Retro Rewind deletion was not applied.");

            var invalidDeletionRejected = false;
            try
            {
                RetroRewindUpdater.ApplyDeletions(root,
                [
                    new RetroDeletion(new Version(6, 13, 1), "unrelated.zip")
                ]);
            }
            catch (InvalidDataException)
            {
                invalidDeletionRejected = true;
            }
            if (!invalidDeletionRejected)
                throw new InvalidDataException("Unsafe root deletion was accepted.");

            var previousVersionPackage = BuildRetroArchive(
                ("RetroRewind6/compat.txt", version),
                ("/6.13.0.zip", [0, 1, 2]));
            using (var buffer = new MemoryStream(previousVersionPackage))
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Read))
                RetroRewindUpdater.ExtractArchiveEntries(archive,
                    root, new Version(6, 13, 1));
            if (!File.Exists(Path.Combine(root, "RetroRewind6", "compat.txt")) ||
                File.Exists(Path.Combine(root, "6.13.0.zip")))
                throw new InvalidDataException("Older root update archive was not ignored.");

            var nested = BuildRetroArchive(("RetroRewind6/version.txt", version));
            var wrapped = BuildRetroArchive(("6.13.0.zip", nested));
            var wrappedRoot = Path.Combine(root, "wrapped");
            using (var buffer = new MemoryStream(wrapped))
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Read))
                RetroRewindUpdater.ExtractArchiveEntries(archive, wrappedRoot, new Version(6, 13, 0));

            if (File.ReadAllText(Path.Combine(wrappedRoot, "RetroRewind6", "version.txt")) != "6.13.0")
                throw new InvalidDataException("Nested Retro Rewind update was not extracted.");

            var invalid = BuildRetroArchive(
                ("RetroRewind6/version.txt", version),
                ("unrelated.zip", [0, 1, 2]));
            var rejected = false;
            try
            {
                using var buffer = new MemoryStream(invalid);
                using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
                RetroRewindUpdater.ExtractArchiveEntries(archive, Path.Combine(root, "invalid"), new Version(6, 13, 0));
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            if (!rejected)
                throw new InvalidDataException("Unexpected root archive entry was accepted.");

            var escape = BuildRetroArchive(
                ("RetroRewind6/version.txt", version),
                ("RetroRewind6/../../../escape.txt", version));
            rejected = false;
            try
            {
                using var buffer = new MemoryStream(escape);
                using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
                RetroRewindUpdater.ExtractArchiveEntries(archive, Path.Combine(root, "escape"), new Version(6, 13, 0));
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            if (!rejected)
                throw new InvalidDataException("Retro Rewind path traversal was accepted.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] BuildRetroArchive(params (string Name, byte[] Data)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                using var output = archive.CreateEntry(file.Name).Open();
                output.Write(file.Data);
            }
        }
        return buffer.ToArray();
    }

    public static void RunBootstrapDiscovery()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "mkwvc-bootstrap-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var wheelWizard = Path.Combine(root, "WheelWizard");
            var dolphinUser = Path.Combine(root, "DolphinUser");
            var rr = Path.Combine(
                dolphinUser,
                "Load",
                "Riivolution",
                "WheelWizard",
                "RetroRewind6");

            Directory.CreateDirectory(Path.Combine(rr, "Binaries"));
            File.WriteAllText(Path.Combine(rr, "version.txt"), "6.12.8");
            File.WriteAllBytes(Path.Combine(rr, "Binaries", "Code.pul"), [1, 2, 3]);
            Directory.CreateDirectory(wheelWizard);
            File.WriteAllText(
                Path.Combine(wheelWizard, "config.json"),
                JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["UserFolderPath"] = dolphinUser,
                    ["GameLocation"] = Path.Combine(root, "game.iso")
                }));

            var recomp = Path.Combine(wheelWizard, "Recomp");
            var install = Path.Combine(recomp, "Install");
            var layout = new PatcherLayout(
                wheelWizard,
                Path.Combine(wheelWizard, "config.json"),
                recomp,
                install,
                Path.Combine(recomp, "Cache"),
                Path.Combine(install, "WiiCompiled-Setup.exe"),
                Path.Combine(install, "install-state.json"));

            var resolved = PatcherPaths.ResolveRetroRoot(layout, state: null);
            if (!Path.GetFullPath(resolved).Equals(
                    Path.GetFullPath(rr),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception(
                    $"Bootstrap RR discovery returned '{resolved}', expected '{rr}'.");
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }
}