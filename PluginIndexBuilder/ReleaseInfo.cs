using Chorizite.Plugins.Models;
using Octokit;
using System;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Chorizite.Core.Plugins;

namespace Chorizite.PluginIndexBuilder {
    public class ReleaseInfo {
        public RepoInfo Repo { get; }
        public Release Release { get; }
        public ReleaseDetailsModel? Existing { get; }

        public ReleaseAsset? ReleaseAsset => Release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip"));

        public bool IsNew => Existing is null;
        public bool HasAssetModifications => Existing is not null && Existing.Updated != ReleaseAsset?.UpdatedAt.UtcDateTime;

        public bool IsValid => ReleaseAsset is not null && Details is not null;

        public ReleaseDetailsModel? Details { get; private set; }
        public PluginManifest? Manifest { get; private set; }

        public ReleaseInfo(RepoInfo repo, Release release, ReleaseDetailsModel? existing) {
            Repo = repo;
            Release = release;
            Existing = existing;
        }

        internal async Task Build() {
            if (IsNew) {
                if (ReleaseAsset is null) {
                    Repo.Log($"No release asset found for {Release.TagName} -> skipping");
                    return;
                }
                Details = await BuildDetails();
            }
            else {
                Details = Existing;
            }
        }

        private async Task<ReleaseDetailsModel?> BuildDetails() {
            if (ReleaseAsset is null) return null;

            var zipPath = await DownloadReleaseAsset();
            if (zipPath is null) return null;

            var extractedPath = await ExtractZip(zipPath);
            if (extractedPath is null) return null;

            var manifestPath = FindManifestFile(extractedPath);
            if (manifestPath is null) return null;

            if (!PluginManifest.TryLoadManifest<PluginManifest>(manifestPath, out var manifest, out var errors)) {
                Repo.Log($"Error loading manifest: {manifestPath}: {errors}");
                return null;
            }

            Manifest = manifest;

            return new ReleaseDetailsModel() {
                Name = ReleaseAsset.Name,
                Version = Manifest.Version,
                Changelog = Release.Body,
                Created = ReleaseAsset.CreatedAt.UtcDateTime,
                Updated = ReleaseAsset.UpdatedAt.UtcDateTime,
                Dependencies = Manifest.Dependencies,
                Downloads = ReleaseAsset.DownloadCount,
                DownloadUrl = ReleaseAsset.BrowserDownloadUrl,
                Environments = Manifest.Environments.ToString().Split(' ').ToList(),
                HasReleaseModifications = false,
                IsBeta = Release.Prerelease || Manifest.Version.Contains("-"),
                Sha256 = await IndexBuilder.CalculateSha256(zipPath)
            };
        }

        /// <summary>
        /// Finds the manifest file in the given directory
        /// </summary>
        /// <param name="rootDirectory"></param>
        /// <param name="searchPattern"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="DirectoryNotFoundException"></exception>
        private string? FindManifestFile(string rootDirectory, string searchPattern = "manifest.json") {
            return Directory.GetFiles(rootDirectory, searchPattern, SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        /// <summary>
        /// Downloads the release asset and returns the path to the zip file on the local file system
        /// </summary>
        /// <returns></returns>
        private async Task<string?> DownloadReleaseAsset() {
            if (ReleaseAsset is null) return null;

            var dirName = Release.TagName.Split('/').Last();
            var zipPath = Path.Combine(Repo.workDirectory, $"{dirName}.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);

            if (Program.Options?.Verbose == true) Repo.Log($"Downloading: {ReleaseAsset.Name}: {ReleaseAsset.BrowserDownloadUrl} -> {zipPath}");
            using (var response = await Program.http.GetAsync(ReleaseAsset.BrowserDownloadUrl)) {
                response.EnsureSuccessStatusCode();
                using (var fileStream = File.Create(zipPath)) {
                    await response.Content.CopyToAsync(fileStream);
                }
            }

            return zipPath;
        }

        /// <summary>
        /// Extracts the zip file to the local file system and returns the path to the extracted directory
        /// </summary>
        /// <param name="zipPath"></param>
        /// <returns></returns>
        private async Task<string?> ExtractZip(string zipPath) {
            var dirName = Release.TagName.Split('/').Last();
            var extractPath = Path.Combine(Repo.workDirectory, $"{dirName}");

            try {
                Directory.Delete(extractPath, true);
                Directory.CreateDirectory(extractPath);
            }
            catch { }

            if (Program.Options?.Verbose == true) Repo.Log($"Extracting: {zipPath}: {extractPath}");
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractPath));

            return extractPath;
        }
    }
}