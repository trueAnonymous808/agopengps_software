using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AgIO.Configurator
{
    public sealed class FirmwareEntry
    {
        public string Group;          // where it is from: the fork's modified ones or Config-O-Matic's folders
        public string Name;
        public string LocalPath;      // where the .hex is (or will be) on this computer
        public bool Modified;         // ours: raw GNSS to AgIO, receiver commands, UM982 HPR

        public bool Available => File.Exists(LocalPath);

        public override string ToString() => (Modified ? "★ " : "") + Group + " / " + Name;
    }

    /// <summary>
    /// The firmwares the configurator can put on a Teensy, from two places:
    ///  - the modified ones (raw GNSS for TDCP, receiver commands, network update, UM982 HPR) from the AgOpenGPS
    ///    fork of Config-O-Matic, its Firmwares/AgOpenGPS-OTA-RawGNSS folder;
    ///  - everything else Config-O-Matic offers, from its original repository, unchanged.
    /// Downloads are kept in a cache, so they are there without internet later. Config-O-Matic's receiver
    /// configuration files come with the original.
    /// </summary>
    public sealed class FirmwareCatalog
    {
        public const string Branch = "main";
        public const string ModifiedFolder = "Firmwares/AgOpenGPS-OTA-RawGNSS/";
        private const string UserAgent = "AgIO-Configurator";

        private sealed class Source
        {
            public string Repo;
            public string CacheName;
            public string Group;
            public bool Modified;
            public Func<string, bool> Wanted;
        }

        private static readonly Source[] Sources =
        {
            // the modified firmwares: only their folder of the fork
            new Source
            {
                Repo = "trueAnonymous808/AOGConfigOMatic",
                CacheName = "AgOpenGPS-OTA-RawGNSS",
                Group = "AgOpenGPS (OTA & RawGNSS)",
                Modified = true,
                Wanted = p => p.StartsWith(ModifiedFolder, StringComparison.Ordinal) && p.EndsWith(".hex", StringComparison.OrdinalIgnoreCase)
            },
            // the original Config-O-Matic: all its firmwares and receiver files
            new Source
            {
                Repo = "lansalot/AOGConfigOMatic",
                CacheName = "ConfigOMatic",
                Group = "Config-O-Matic",
                Modified = false,
                Wanted = p => (p.StartsWith("Firmwares/", StringComparison.Ordinal) && !p.StartsWith(ModifiedFolder, StringComparison.Ordinal)
                                  && p.EndsWith(".hex", StringComparison.OrdinalIgnoreCase))
                              || (p.StartsWith("AOGConfigOMatic/", StringComparison.Ordinal) && p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            }
        };

        public static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgOpenGPS", "FirmwareCache");
        private static string ReceiversDir => Path.Combine(CacheDir, "Receivers");

        public List<FirmwareEntry> Entries { get; } = new List<FirmwareEntry>();

        private static string SourceDir(Source s) => Path.Combine(CacheDir, s.CacheName);
        private static string IndexFile(Source s) => Path.Combine(CacheDir, s.CacheName + "-index.txt");

        /// <summary>What is on this computer: the cached downloads (no internet needed).</summary>
        public void LoadLocal()
        {
            Entries.Clear();
            foreach (Source s in Sources)
            {
                foreach (KeyValuePair<string, string> f in ReadIndex(s))
                    if (f.Key.EndsWith(".hex", StringComparison.OrdinalIgnoreCase)) Entries.Add(Entry(s, f.Key));
            }
        }

        private static FirmwareEntry Entry(Source s, string path)
        {
            string rel = path.StartsWith(ModifiedFolder, StringComparison.Ordinal) ? path.Substring(ModifiedFolder.Length)
                : path.StartsWith("Firmwares/", StringComparison.Ordinal) ? path.Substring("Firmwares/".Length) : path;
            string dir = Path.GetDirectoryName(rel.Replace('/', Path.DirectorySeparatorChar)) ?? "";
            return new FirmwareEntry
            {
                Group = s.Group + (dir.Length > 0 ? " / " + dir.Replace(Path.DirectorySeparatorChar, '/') : ""),
                Name = Path.GetFileNameWithoutExtension(rel),
                Modified = s.Modified,
                LocalPath = LocalFor(s, path)
            };
        }

        private static string LocalFor(Source s, string path)
        {
            if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return Path.Combine(ReceiversDir, Path.GetFileName(path));
            return Path.Combine(SourceDir(s), path.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// The current lists from GitHub, and every file not cached yet or changed since. A source that cannot be
        /// reached keeps its cached files. progress: (done, total, name). Returns how many files were downloaded.
        /// </summary>
        public int Refresh(Action<int, int, string> progress, CancellationToken cancel, Action<string> problem)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            int downloaded = 0;
            foreach (Source s in Sources)
            {
                try
                {
                    downloaded += RefreshSource(s, progress, cancel);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    problem?.Invoke(s.Repo + ": " + ex.Message);
                }
            }
            LoadLocal();
            return downloaded;
        }

        private static int RefreshSource(Source s, Action<int, int, string> progress, CancellationToken cancel)
        {
            string tree;
            using (var web = NewClient())
                tree = web.DownloadString("https://api.github.com/repos/" + s.Repo + "/git/trees/" + Branch + "?recursive=1");

            // path / blob sha pairs, without a JSON library: "path":"...","mode":"...","type":"blob","sha":"..."
            var files = new List<KeyValuePair<string, string>>();
            foreach (Match m in Regex.Matches(tree, "\"path\"\\s*:\\s*\"([^\"]+)\"[^{}]*?\"type\"\\s*:\\s*\"blob\"[^{}]*?\"sha\"\\s*:\\s*\"([0-9a-f]+)\""))
            {
                string path = Regex.Unescape(m.Groups[1].Value);
                if (s.Wanted(path)) files.Add(new KeyValuePair<string, string>(path, m.Groups[2].Value));
            }

            Dictionary<string, string> cached = ReadIndex(s);
            Directory.CreateDirectory(CacheDir);
            int done = 0, downloaded = 0;
            foreach (KeyValuePair<string, string> f in files)
            {
                cancel.ThrowIfCancellationRequested();
                string local = LocalFor(s, f.Key);
                progress?.Invoke(done, files.Count, f.Key);
                if (!File.Exists(local) || !cached.TryGetValue(f.Key, out string sha) || sha != f.Value)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(local));
                    string url = "https://raw.githubusercontent.com/" + s.Repo + "/" + Branch + "/" + string.Join("/", f.Key.Split('/').Select(Uri.EscapeDataString));
                    using (var web = NewClient())
                        web.DownloadFile(url, local + ".part");
                    if (File.Exists(local)) File.Delete(local);
                    File.Move(local + ".part", local);
                    downloaded++;
                }
                done++;
            }
            File.WriteAllLines(IndexFile(s), files.Select(f => f.Key + "\t" + f.Value), Encoding.UTF8);
            progress?.Invoke(done, files.Count, "");
            return downloaded;
        }

        private static Dictionary<string, string> ReadIndex(Source s)
        {
            var d = new Dictionary<string, string>();
            if (!File.Exists(IndexFile(s))) return d;
            foreach (string line in File.ReadAllLines(IndexFile(s)))
            {
                string[] p = line.Split('\t');
                if (p.Length >= 2) d[p[0]] = p[1];
            }
            return d;
        }

        /// <summary>A Config-O-Matic receiver file (ConfigUM982D.txt, Single.txt, ...) from the cache, null if not there.</summary>
        public static string ReceiverFile(string name)
        {
            string p = Path.Combine(ReceiversDir, name);
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }

        public static IEnumerable<string> ReceiverFiles()
        {
            return Directory.Exists(ReceiversDir) ? Directory.GetFiles(ReceiversDir, "*.txt").Select(Path.GetFileName).OrderBy(n => n) : Enumerable.Empty<string>();
        }

        private static WebClient NewClient()
        {
            var web = new WebClient();
            web.Headers[HttpRequestHeader.UserAgent] = UserAgent;
            return web;
        }
    }
}
