using Octokit;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Chorizite.PluginIndexBuilder {
    public class ReleaseInfo {
        private RepositoryInfo repositoryInfo;
        private Release release;
        internal string manifestPath;
        private ReleaseAsset asset;
        internal string zipPath;
        internal string manifestVersion;
        internal string manifestName;
        internal string manifestDescription;
        internal string manifestAuthor;
        internal bool HasPackage = false;

        public string Name { get; set; } = "";
        public bool IsBeta { get; private set; }
        public string Version { get; set; } = "";
        public string Changelog { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public DateTime Date { get; set; }
        public JsonObject Manifest { get; set; }
        public string Hash { get; set; } = "";

        [JsonIgnore]
        public bool IsNew => repositoryInfo?.ExistingReleaseInfo?.Releases.Find(r => r.Version == Version) == null;

        [JsonIgnore]
        public string OldHash => repositoryInfo?.ExistingReleaseInfo?.Releases.FirstOrDefault(r => r.Version == Version).Hash;

        public ReleaseInfo(RepositoryInfo respositoryInfo, Release release, ReleaseAsset asset, string manifestPath, string zipPath) {
            this.repositoryInfo = respositoryInfo;
            this.release = release;
            this.manifestPath = manifestPath;
            this.asset = asset;
            this.zipPath = zipPath;
        }

        public ReleaseInfo() { }

        internal async Task Build() {
            try {
                var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)).AsObject();

                manifestVersion = manifest["version"]?.ToString() ?? "0.0.0";
                manifestName = manifest["name"]?.ToString() ?? "";
                manifestDescription = manifest["description"]?.ToString() ?? "";
                manifestAuthor = manifest["author"]?.ToString() ?? "";

                Name = release.Name;
                IsBeta = release.Prerelease;
                Version = manifestVersion;
                Changelog = release.Body;
                DownloadUrl = asset.BrowserDownloadUrl;
                Date = (release.PublishedAt ?? release.CreatedAt).UtcDateTime;
                Manifest = manifest;
                Hash = CalculateMD5(zipPath);
            }
            catch (Exception e) {
                Console.WriteLine($"Error building release: {e.Message}");
            }
        }
        static string CalculateMD5(string filename) {
            using (var md5 = MD5.Create()) {
                using (var stream = File.OpenRead(filename)) {
                    var hash = md5.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        private void Log(string v) {
            Console.WriteLine($"[{repositoryInfo.repoPath}@{release.TagName}] {v}");
        }
    }
}