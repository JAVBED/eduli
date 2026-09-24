using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Eduli;

internal static class Program
{
    internal static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || (args.Length == 1 && new[] { "help", "--help", "-h" }.Contains(args[0])))
            {
                Console.WriteLine("eduli 1.0.0 - classic MinecraftEdu launcher\n\n  eduli help       Help and bundled versions\n  eduli <version>  Prepare and launch a local game\n\nExamples: eduli 1.8.9 | eduli 1.7.10\nJava 8 is downloaded automatically when needed.\nEDULI_USERNAME sets the player name; EDULI_HOME sets the data directory.\nEDULI_OFFLINE=1 disables launcher downloads.\nEDULI_JAVA selects an existing Java 8. EDULI_BUNDLE selects a game bundle.\n");
                if (File.Exists(LocalBundle.BundlePath))
                    foreach (var version in LocalBundle.Catalog()) Console.WriteLine("  " + version["id"] + (LocalBundle.Supports((string)version["id"]) ? "" : " [other platform]"));
                else Console.WriteLine("Game bundle not found. Put minecraftedu.bundle.zip beside eduli, or set EDULI_BUNDLE.");
                Console.WriteLine("\nData: " + Path.GetFullPath(Platform.DataRoot));
                return 0;
            }
            if (args.Length != 1 || !Regex.IsMatch(args[0], @"^[A-Za-z0-9][A-Za-z0-9._-]*$")) throw new ArgumentException("Usage: eduli help | eduli <version>");
            if (!Platform.SupportedArchitecture) throw new InvalidOperationException("MinecraftEdu requires the x64 build. On Apple Silicon run the macOS x64 build with Rosetta 2.");
            if (!File.Exists(LocalBundle.BundlePath)) throw new IOException("Game bundle not found: " + LocalBundle.BundlePath + ". Download minecraftedu.bundle.zip from the eduli release and place it beside the executable.");
            var selected = LocalBundle.Select(args[0]);
            if (selected == null) throw new ArgumentException("Unknown MinecraftEdu version '" + args[0] + "'. Run eduli help.");
            return LocalBundle.Launch(selected, Path.GetFullPath(Platform.DataRoot), Environment.GetEnvironmentVariable("EDULI_OFFLINE") == "1");
        }
        catch (Exception ex) { Console.Error.WriteLine("eduli: " + ex.Message); return ex is ArgumentException ? 2 : 1; }
    }

    internal static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') || name.Contains(':'))
            throw new InvalidDataException("Invalid file name: " + name);
        return name;
    }

    internal static string Contained(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        path = path.Replace('\\', '/');
        if (path.Contains(':') && !Path.IsPathRooted(path)) throw new InvalidDataException("Invalid path: " + path);
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.Equals(fullRoot, comparison) && !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Path escapes its game/cache directory: " + path);
        // Existing links must not redirect extracted files outside the instance.
        for (string current = fullPath; current != null && !current.Equals(fullRoot, comparison); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Refusing to extract through a symbolic link: " + current);
        return fullPath;
    }

    internal static void Extract(string file, string destination, bool natives)
    {
        using var zip = ZipFile.OpenRead(file);
        foreach (var entry in zip.Entries)
        {
            if (natives && entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
            string path = Contained(destination, entry.FullName);
            if (entry.Name.Length == 0) Directory.CreateDirectory(path);
            else { Directory.CreateDirectory(Path.GetDirectoryName(path)); entry.ExtractToFile(path, true); }
        }
    }

    internal static bool Valid(string path, long size, string hash)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0 || (size > 0 && new FileInfo(path).Length != size)) return false;
        if (hash == null) return true;
        using var stream = File.OpenRead(path);
        using HashAlgorithm algorithm = hash.Length == 64 ? SHA256.Create() : SHA1.Create();
        return Convert.ToHexString(algorithm.ComputeHash(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    internal static void Download(string url, string destination, long size = 0, bool renew = false, string hash = null)
    {
        if (Valid(destination, size, hash) && !renew) return;
        if (Environment.GetEnvironmentVariable("EDULI_OFFLINE") == "1") throw new IOException("Missing cached file while offline: " + destination);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new InvalidDataException("An HTTPS download URL is required.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            Console.WriteLine("Downloading " + Path.GetFileName(destination) + "...");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using (var input = response.Content.ReadAsStream())
            using (var output = File.Create(temporary))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                input.CopyToAsync(output, timeout.Token).GetAwaiter().GetResult();
            }
            if (!Valid(temporary, size, hash)) throw new InvalidDataException("Downloaded file failed checksum/size verification: " + url);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static IEnumerable<string> Expand(string template, Dictionary<string, string> values)
    {
        var token = new StringBuilder();
        bool quoted = false;
        foreach (char c in (template ?? "") + " ")
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (token.Length == 0) continue;
                string value = Regex.Replace(token.ToString(), @"\{([A-Za-z]+)\}", m => values.TryGetValue(m.Groups[1].Value, out var replacement)
                    ? replacement ?? "" : throw new InvalidDataException("Unknown launch placeholder " + m.Value));
                token.Clear(); yield return value;
            }
            else token.Append(c);
        }
        if (quoted) throw new InvalidDataException("Unclosed quote in launch manifest.");
    }
}
