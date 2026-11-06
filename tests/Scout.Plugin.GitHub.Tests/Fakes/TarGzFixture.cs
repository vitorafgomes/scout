using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Scout.Plugin.GitHub.Tests.Fakes;

/// <summary>A gzip tarball the way GitHub serves one: a PAX global header first, then every path under a top folder named owner-repo-sha.</summary>
internal static class TarGzFixture
{
    public const string TopFolder = "octocat-hello-abc1234";

    public static byte[] Build(params (string Path, byte[] Content)[] entries) => Build(TopFolder, entries);

    public static byte[] Build(string topFolder, params (string Path, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = "abc1234def5678" }));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, topFolder + "/"));
            foreach (var (path, content) in entries)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, path.StartsWith('/') ? path[1..] : topFolder + "/" + path)
                {
                    DataStream = new MemoryStream(content),
                };
                tar.WriteEntry(entry);
            }
        }

        return buffer.ToArray();
    }

    public static (string Path, byte[] Content) Text(string path, string text) => (path, Encoding.UTF8.GetBytes(text));
}
