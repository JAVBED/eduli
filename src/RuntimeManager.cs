using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Eduli
{
    internal static class RuntimeManager
    {
        internal static bool Compatible(string executable)
        {
            if (string.IsNullOrWhiteSpace(executable)) return false;
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo(executable, "-XshowSettings:properties -version") {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
                    };
                    process.Start();
                    var stderr = process.StandardError.ReadToEndAsync();
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    if (!process.WaitForExit(15000)) { process.Kill(); return false; }
                    string output = stderr.Result + stdout.Result;
                    return process.ExitCode == 0 && Regex.IsMatch(output, "version \\\"1\\.8[.\\\"]") &&
                        !output.Contains("sun.arch.data.model = 32") && !output.Contains("os.arch = aarch64");
                }
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (IOException) { return false; }
        }

        internal static void ExtractRuntime(string archive, string destination)
        {
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { Program.Extract(archive, destination, false); return; }
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            var links = new List<(string Path, string Target)>();
            TarEntry entry;
            while ((entry = reader.GetNextEntry()) != null)
            {
                string path = Program.Contained(destination, entry.Name);
                if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(path); continue; }
                if (entry.EntryType == TarEntryType.SymbolicLink)
                {
                    string target = Program.Contained(destination, Path.Combine(Path.GetDirectoryName(entry.Name), entry.LinkName));
                    links.Add((path, target)); continue;
                }
                if (entry.EntryType != TarEntryType.RegularFile && entry.EntryType != TarEntryType.V7RegularFile) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var output = File.Create(path)) entry.DataStream.CopyTo(output);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, entry.Mode & (UnixFileMode)511);
            }
            // Copy internal link targets after regular files are extracted, without creating traversable links.
            foreach (var link in links)
            {
                if (!File.Exists(link.Target)) throw new IOException("Missing Java archive link target: " + link.Target);
                Directory.CreateDirectory(Path.GetDirectoryName(link.Path)); File.Copy(link.Target, link.Path);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(link.Path, File.GetUnixFileMode(link.Target));
            }
        }

        internal static string Find(string root, bool offline)
        {
            string runtime = Path.Combine(root, "runtime");
            string pointer = Path.Combine(runtime, "java8.path");
            var home = Environment.GetEnvironmentVariable("JAVA_HOME");
            var candidates = new[] {
                Environment.GetEnvironmentVariable("EDULI_JAVA"),
                File.Exists(pointer) ? Program.Contained(runtime, File.ReadAllText(pointer).Trim()) : null,
                string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, "bin", Platform.JavaName), Platform.JavaName
            };
            foreach (string candidate in candidates)
                if (Compatible(candidate)) return candidate;
            if (offline) throw new IOException("No compatible Java 8 is available. Run once without EDULI_OFFLINE to download it automatically.");
            Directory.CreateDirectory(runtime);
            using (var installationLock = new FileStream(Path.Combine(runtime, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Console.WriteLine("No compatible Java 8 found. Downloading Eclipse Temurin Java 8...");
                string architecture = "x64";
                string endpoint = "https://api.adoptium.net/v3/assets/latest/8/hotspot?architecture=" + architecture + "&image_type=jre&os=" + Platform.AdoptiumOS + "&vendor=eclipse";
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                JArray releases = JArray.Parse(client.GetStringAsync(endpoint).GetAwaiter().GetResult());
                var package = releases.Select(r => r["binary"]["package"]).FirstOrDefault();
                if (package == null) throw new IOException("Adoptium returned no Java 8 runtime for " + architecture + ".");
                string hash = (string)package["checksum"];
                string url = (string)package["link"];
                long size = (long)package["size"];
                if (hash == null || !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$") || new Uri(url).Scheme != "https" || size <= 0)
                    throw new IOException("Invalid Java download metadata.");
                string zip = Path.Combine(runtime, hash + (Platform.OS == "windows" ? ".zip" : ".tar.gz"));
                Program.Download(url, zip, size, false, hash);
                string destination = Path.Combine(runtime, "java8-" + hash.Substring(0, 16));
                string staging = Path.Combine(runtime, ".staging-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(staging);
                    ExtractRuntime(zip, staging);
                    string executable = Directory.GetFiles(staging, Platform.JavaName, SearchOption.AllDirectories)
                        .FirstOrDefault(p => Path.GetFileName(Path.GetDirectoryName(p)).Equals("bin", StringComparison.OrdinalIgnoreCase));
                    if (executable == null || !Compatible(executable)) throw new IOException("The downloaded runtime did not pass the Java 8 check.");
                    string relative = executable.Substring(staging.Length + 1);
                    // Use a fresh directory if a previous installation is damaged; never overwrite a running JVM.
                    if (Directory.Exists(destination)) destination += "-" + Guid.NewGuid().ToString("N");
                    Directory.Move(staging, destination);
                    executable = Path.Combine(destination, relative);
                    string temporaryPointer = pointer + ".tmp";
                    File.WriteAllText(temporaryPointer, executable.Substring(runtime.Length + 1));
                    if (File.Exists(pointer)) File.Replace(temporaryPointer, pointer, null);
                    else File.Move(temporaryPointer, pointer);
                    Console.WriteLine("Java 8 installed for eduli.");
                    return executable;
                }
                finally
                {
                    if (Directory.Exists(staging)) Directory.Delete(Program.Contained(runtime, staging), true);
                }
            }
        }
    }
}
