using Discord;
using Octokit;
using SixLabors.ImageSharp.Drawing;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static System.Net.WebRequestMethods;

namespace Chorizite.PluginIndexBuilder {
    public class ReleaseInfo {
        private RepositoryInfo repositoryInfo;
        internal Release release;
        internal string manifestPath = "";
        internal ReleaseAsset asset;
        internal string zipPath = "";
        internal string manifestVersion = "";
        internal string manifestName = "";
        internal string manifestDescription = "";
        internal string manifestAuthor = "";
        internal bool HasPackage = false;

        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsBeta { get; private set; }
        public string Version { get; set; } = "";
        public string Changelog { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public DateTime Date { get; set; } = DateTime.Now;
        public JsonObject? Manifest { get; set; }
        public string Hash { get; set; } = "";

        [JsonIgnore]
        public bool IsNew => repositoryInfo?.ExistingReleaseInfo?.Releases.Find(r => r.Version == Version) == null;

        [JsonIgnore]
        public string? OldHash => repositoryInfo?.ExistingReleaseInfo?.Releases.FirstOrDefault(r => r.Version == Version)?.Hash;

        public ReleaseInfo(RepositoryInfo respositoryInfo, Release release, ReleaseAsset asset) {
            this.repositoryInfo = respositoryInfo;
            this.release = release;
            this.asset = asset;
        }

        internal async Task Build() {
            try {
                if (release is null) {
                    throw new Exception($"No manifest found for release");
                }

                var x = await DownloadAndExtractRelease(release, asset);
                if (string.IsNullOrEmpty(x.Item1)) {
                    Log($"Error: No zip found for release: {release.TagName}");
                    return;
                }
                zipPath = x.Item2;
                manifestPath = FindManifestFile(x.Item1);
                if (string.IsNullOrEmpty(manifestPath)) {
                    Log($"Error: No manifest found for release: {release.TagName}");
                    return;
                }

                var manifest = JsonNode.Parse(System.IO.File.ReadAllText(manifestPath))?.AsObject();

                if (manifest is null) {
                    Log($"Error: No manifest found for release: {release.TagName}");
                    return;
                }

                manifestVersion = manifest["version"]?.ToString() ?? "0.0.0";
                manifestName = manifest["name"]?.ToString() ?? "";
                manifestDescription = manifest["description"]?.ToString() ?? "";
                manifestAuthor = manifest["author"]?.ToString() ?? "";

                Name = release.Name;
                IsBeta = release.Prerelease;
                Version = manifestVersion;
                Changelog = release.Body;
                DownloadUrl = asset?.BrowserDownloadUrl ?? "";
                Date = (release.PublishedAt ?? release.CreatedAt).UtcDateTime;
                Manifest = manifest;
                Hash = CalculateMD5(zipPath);
            }
            catch (Exception e) {
                Console.WriteLine($"Error building release: {e}");
            }
        }

        private string? FindManifestFile(string rootDirectory, string searchPattern = "manifest.json") {
            if (string.IsNullOrEmpty(rootDirectory))
                throw new ArgumentException("Root directory cannot be null or empty", nameof(rootDirectory));

            if (!Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException($"Directory not found: {rootDirectory}");

            // First, search in the current directory
            var manifestInCurrentDir = Directory.GetFiles(rootDirectory, searchPattern, SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

            if (manifestInCurrentDir != null)
                return manifestInCurrentDir;

            foreach (var dir in Directory.GetDirectories(rootDirectory)) {
                var manifestInSubDir = FindManifestFile(dir, searchPattern);
                if (manifestInSubDir != null)
                    return manifestInSubDir;
            }

            return null;
        }

        private async Task<Tuple<string, string>?> DownloadAndExtractRelease(Release release, ReleaseAsset zip) {
            try {
                if (release is null) throw new Exception($"No release");
                if (zip is null) throw new Exception($"No zip found for release: {release.TagName}");

                var dirName = release.TagName.Split('/').Last();
                var zipPath = System.IO.Path.Combine(repositoryInfo.workDirectory, $"{dirName}.zip");
                var extractPath = System.IO.Path.Combine(repositoryInfo.workDirectory, $"{dirName}");

                if (System.IO.File.Exists(zipPath)) System.IO.File.Delete(zipPath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);

                if (Program.Options?.Verbose == true) Log($"Downloading: {zip.Name}: {zip.BrowserDownloadUrl} -> {zipPath}");
                using (var response = await Program.http.GetAsync(zip.BrowserDownloadUrl)) {
                    response.EnsureSuccessStatusCode();
                    using (var fileStream = System.IO.File.Create(zipPath)) {
                        await response.Content.CopyToAsync(fileStream);
                    }
                }

                Directory.CreateDirectory(extractPath);
                var p = zipPath;
                await Task.Run(() => ZipFile.ExtractToDirectory(p, extractPath));
                if (Program.Options?.Verbose == true) Log($"Extracted: {zip.Name}: {extractPath}");

                return new Tuple<string, string>(extractPath, zipPath);
            }
            catch (Exception e) {
                Log($"Error downloading release ({release?.Name}): {e}");
                return null;
            }
        }

        static string CalculateMD5(string filename) {
            using (var md5 = MD5.Create()) {
                using (var stream = System.IO.File.OpenRead(filename)) {
                    var hash = md5.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        private void Log(string v) {
            repositoryInfo.Log(v);
        }
    }
}