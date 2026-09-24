using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Eduli
{
    internal static class LocalBundle
    {
        internal static string BundlePath { get { return Environment.GetEnvironmentVariable("EDULI_BUNDLE") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "minecraftedu.bundle.zip"); } }

        internal static JArray Catalog()
        {
            if (!File.Exists(BundlePath)) return new JArray();
            using (var bundle = ZipFile.OpenRead(BundlePath))
            using (var reader = new StreamReader(bundle.GetEntry("catalog.json").Open())) return JArray.Parse(reader.ReadToEnd());
        }

        internal static bool Supports(string id)
        {
            if (id.EndsWith("-linux")) return Platform.OS == "linux";
            if (id.EndsWith("-mac")) return Platform.OS == "osx";
            if (id.EndsWith("-windowsxp") || id.EndsWith("-windows7vista")) return Platform.OS == "windows";
            return true;
        }

        internal static JObject Select(string input)
        {
            var catalog = Catalog().OfType<JObject>();
            return catalog.FirstOrDefault(v => (string)v["id"] == input) ?? catalog
                .Where(v => Supports((string)v["id"]) && ((string)v["id"]).Contains("classroom") &&
                    (((string)v["id"]).StartsWith(input + "_", StringComparison.Ordinal) || ((string)v["id"]).StartsWith(input + "-", StringComparison.Ordinal)))
                .OrderByDescending(v => (string)v["date"])
                .ThenBy(v => ((string)v["id"]).Contains("macbook") ? 1 : 0)
                .ThenBy(v => ((string)v["id"]).Contains("windowsxp") ? 1 : 0).FirstOrDefault();
        }

        internal static int Launch(JObject version, string root, bool offline)
        {
            if (!Supports((string)version["id"])) throw new ArgumentException("This installer variant targets a different operating system. Run eduli help.");
            string id = Program.SafeName((string)version["id"]);
            string username = Environment.GetEnvironmentVariable("EDULI_USERNAME") ?? "Player";
            if (!Regex.IsMatch(username, @"^[A-Za-z0-9_]{1,16}$")) throw new ArgumentException("EDULI_USERNAME must contain 1-16 letters, digits, or underscores.");
            string instance = Path.Combine(root, "instances", id);
            Directory.CreateDirectory(instance);
            using (var instanceLock = new FileStream(Path.Combine(instance, ".launch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                string java = RuntimeManager.Find(root, offline);
                Prepare(version, instance);
                string game = Path.Combine(instance, (bool)version["modern"] ? ".minecraft" : "minecraft");
                string natives = Path.Combine(game, "bin", "natives");
                Directory.CreateDirectory(natives);
                var arguments = new List<string> { "-Xms256M", "-Xmx1024M", "-Djava.library.path=" + natives,
                    "-Dfml.ignoreInvalidMinecraftCertificates=true", "-Dlog4j2.formatMsgNoLookups=true" };
                if (Platform.OS == "osx") arguments.Add("-XstartOnFirstThread");
                var jars = new List<string>();
                string main;
                IEnumerable<string> gameArguments;
                if ((bool)version["modern"])
                {
                    string metadataPath = Path.Combine(game, "versions", "mceduforge", "mceduforge.json");
                    var metadata = JObject.Parse(File.ReadAllText(metadataPath));
                    string jarId = Program.SafeName((string)metadata["jar"] ?? (string)metadata["id"]);
                    string jar = Path.Combine(game, "versions", jarId, jarId + ".jar");
                    string eduJar = Path.Combine(instance, "launcher_res", "jar", "minecraftedu.jar");
                    if (jarId == "mceduforge" && File.Exists(eduJar)) { Directory.CreateDirectory(Path.GetDirectoryName(jar)); File.Copy(eduJar, jar, true); }
                    Require(jar); jars.Add(jar);
                    foreach (var library in metadata["libraries"] ?? new JArray())
                    {
                        if (!Allowed(library)) continue;
                        string classifier = library["natives"] == null ? null : (string)library["natives"][Platform.OS];
                        if (library["natives"] != null && classifier == null) continue;
                        if (classifier != null) classifier = classifier.Replace("${arch}", "64");
                        var parts = ((string)library["name"]).Split(':');
                        if (parts.Length < 3 || parts.Length > 4) throw new InvalidDataException("Invalid bundled library coordinate.");
                        string file = parts[1] + "-" + parts[2] + (classifier != null ? "-" + classifier : parts.Length == 4 ? "-" + parts[3] : "") + ".jar";
                        string path = Program.Contained(Path.Combine(game, "libraries"), parts[0].Replace('.', '/') + "/" + parts[1] + "/" + parts[2] + "/" + file);
                        Require(path);
                        if (classifier != null) Program.Extract(path, natives, true);
                        else if (!jars.Contains(path)) jars.Add(path);
                    }
                    main = (string)metadata["mainClass"];
                    string assetName = (string)metadata["assets"] ?? "legacy";
                    string indexes = Path.Combine(game, "assets", "indexes");
                    if (!File.Exists(Path.Combine(indexes, assetName + ".json")) && Directory.Exists(indexes))
                        assetName = Path.GetFileNameWithoutExtension(Directory.GetFiles(indexes, "*.json").OrderBy(p => p).FirstOrDefault()) ?? assetName;
                    string legacyAssets = File.Exists(Path.Combine(indexes, "legacy.json"))
                        ? Path.Combine(game, "assets", "virtual", "legacy") : Path.Combine(game, "assets");
                    var replacements = new Dictionary<string, string> {
                        { "auth_player_name", username }, { "version_name", (string)metadata["id"] },
                        { "game_directory", game }, { "assets_root", Path.Combine(game, "assets") },
                        { "game_assets", legacyAssets },
                        { "assets_index_name", assetName },
                        { "auth_uuid", "00000000000000000000000000000000" }, { "auth_access_token", "-" },
                        { "auth_session", "-" }, { "user_properties", "{}" }, { "user_type", "legacy" }
                    };
                    gameArguments = Expand((string)metadata["minecraftArguments"], replacements);
                    PrepareVirtualAssets(game, assetName);
                }
                else
                {
                    string jar = Program.Contained(instance, (string)version["legacyJar"]);
                    Require(jar); jars.Add(jar);
                    string legacyLibs = Platform.OS == "windows" ? Path.Combine(game, "bin") : Path.Combine(instance, "platform", "legacy", Platform.OS);
                    if (Platform.OS != "windows")
                    {
                        Program.Extract(Path.Combine(legacyLibs, "lwjgl-natives.jar"), natives, true);
                        Program.Extract(Path.Combine(legacyLibs, "jinput-natives.jar"), natives, true);
                    }
                    foreach (string file in new[] { "lwjgl.jar", "lwjgl_util.jar", "jinput.jar" })
                    { string path = Path.Combine(legacyLibs, file); Require(path); jars.Add(path); }
                    string libraries = Path.Combine(game, "lib");
                    if (Directory.Exists(libraries)) jars.AddRange(Directory.GetFiles(libraries, "*.jar"));
                    main = "net.minecraft.client.Minecraft";
                    gameArguments = new[] { username, "-" };
                }
                arguments.Add("-cp"); arguments.Add(string.Join(Path.PathSeparator, jars)); arguments.Add(main); arguments.AddRange(gameArguments);
                var start = new ProcessStartInfo(java) { WorkingDirectory = (bool)version["modern"] ? game : instance, UseShellExecute = false };
                foreach (string argument in arguments) start.ArgumentList.Add(argument);
                // Legacy clients use an isolated working directory.
                start.EnvironmentVariables["APPDATA"] = instance;
                Console.WriteLine("Launching local MinecraftEdu " + id + " as " + username + "...");
                using (var process = Process.Start(start)) { process.WaitForExit(); return process.ExitCode; }
            }
        }

        internal static void Prepare(JObject version, string instance)
        {
            string marker = Path.Combine(instance, ".bundle-installed");
            string identity;
            using (var sha = SHA256.Create()) identity = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(version.ToString() + "layout-v3-" + Platform.OS)));
            if (File.Exists(marker) && File.ReadAllText(marker) == identity) return;
            Console.WriteLine("Preparing bundled MinecraftEdu " + version["id"] + "...");
            using (var bundle = ZipFile.OpenRead(BundlePath))
            {
                foreach (var file in ((JObject)version["files"]).Properties())
                {
                    string destination = Program.Contained(instance, (bool)version["modern"] && file.Name.StartsWith("minecraft/") ? ".minecraft/" + file.Name.Substring(10) : file.Name);
                    // Preserve worlds and user settings if preparation is retried after interruption.
                    if (File.Exists(destination) && (file.Name.StartsWith("minecraft/saves/") || file.Name.StartsWith("minecraft/options") || file.Name.EndsWith(".ini") || file.Name.Contains("/config/"))) continue;
                    string hash = (string)file.Value;
                    if (!Regex.IsMatch(hash, "^[a-f0-9]{64}$")) throw new InvalidDataException("Invalid bundle checksum.");
                    var entry = bundle.GetEntry("objects/" + hash);
                    if (entry == null) throw new InvalidDataException("Missing bundle object: " + hash);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    string temporary = destination + ".eduli-part";
                    try
                    {
                        entry.ExtractToFile(temporary, true);
                        using (var sha = SHA256.Create())
                        using (var input = File.OpenRead(temporary))
                            if (BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant() != hash)
                                throw new InvalidDataException("Corrupt bundle object: " + file.Name);
                        if (File.Exists(destination)) File.Replace(temporary, destination, null);
                        else File.Move(temporary, destination);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
            File.WriteAllText(marker, identity);
        }

        internal static bool Allowed(JToken library)
        {
            if (library["rules"] == null) return true;
            bool allowed = false;
            foreach (var rule in library["rules"])
            {
                var os = rule["os"];
                if (os != null && (string)os["name"] != Platform.OS) continue;
                allowed = (string)rule["action"] == "allow";
            }
            return allowed;
        }

        static IEnumerable<string> Expand(string template, Dictionary<string, string> replacements)
        {
            // Convert Mojang placeholders to the existing argument tokenizer's format.
            var numbered = new Dictionary<string, string>();
            string converted = Regex.Replace(template ?? "", @"\$\{([a-z_]+)\}", m => {
                string key = m.Groups[1].Value;
                if (!replacements.ContainsKey(key)) throw new InvalidDataException("Unknown game placeholder " + key);
                string simple = key.Replace("_", ""); numbered[simple] = replacements[key]; return "{" + simple + "}";
            });
            return Program.Expand(converted, numbered);
        }

        static void PrepareVirtualAssets(string game, string name)
        {
            string index = Path.Combine(game, "assets", "indexes", Program.SafeName(name) + ".json");
            if (!File.Exists(index)) return;
            var data = JObject.Parse(File.ReadAllText(index));
            if ((bool?)data["virtual"] != true && name != "legacy") return;
            foreach (var asset in ((JObject)data["objects"]).Properties())
            {
                string hash = (string)asset.Value["hash"];
                if (!Regex.IsMatch(hash, "^[a-f0-9]{40}$")) throw new InvalidDataException("Invalid asset hash.");
                string source = Path.Combine(game, "assets", "objects", hash.Substring(0, 2), hash);
                if (!File.Exists(source)) continue;
                string destination = Program.Contained(Path.Combine(game, "assets", "virtual", name), asset.Name);
                if (!File.Exists(destination)) { Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(source, destination); }
            }
        }

        static void Require(string path)
        { if (!File.Exists(path)) throw new IOException("This installer is missing a required local game file: " + path); }
    }
}
