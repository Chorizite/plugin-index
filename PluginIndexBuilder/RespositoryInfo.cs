using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Octokit;
using Octokit.Internal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Chorizite.PluginIndexBuilder {
    public class RespositoryInfo {
        internal static readonly HttpClient http = new HttpClient();

        internal Options options;
        internal GitHubClient client;
        private Logger _log;

        private static List<string> CorePlugins = ["Lua", "RmlUi", "Launcher", "AC", "PluginManagerUI"];

        internal string repoPath => RepoUrl.Replace("https://github.com/", "");
        internal string repoOwner => repoPath.Split("/")[0];
        internal string repoName => repoPath.Split("/")[1];
        internal string workDirectory => Path.Combine(options.WorkDirectory, repoOwner, repoName);
        internal ConcurrentBag<ReleaseInfo> releaseInfos = new ConcurrentBag<ReleaseInfo>();

        public string Name { get; set; }
        public string PackageId => IsCorePlugin ? $"Chorizite.Plugins.{Name}" : Name ;
        public bool IsCorePlugin => CorePlugins.Contains(Name);

        private SourceCacheContext nugetCache;

        public string Description { get; set; }
        public string RepoUrl { get; set; }
        public ReleaseInfo? Latest { get; set; }
        public ReleaseInfo? LatestBeta { get; set; }
        public string Author { get; set; }

        public List<ReleaseInfo>? Releases = [];
        private IReadOnlyList<PackageVersion>? _versions;
        private IEnumerable<NuGetVersion> nugetVersions;
        private SourceRepository nugetRepo;

        public RespositoryInfo(string name, string repoUrl, Octokit.GitHubClient client, Options options) {
            this.RepoUrl = repoUrl;
            this.options = options;
            this.client = client;
            _log = new Logger(name);

            Name = name;

            nugetCache = new SourceCacheContext();
            nugetCache.NoCache = true;
            nugetRepo = NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3($"https://nuget.pkg.github.com/Chorizite/index.json");
            nugetRepo.PackageSource.Credentials = new PackageSourceCredential($"https://nuget.pkg.github.com/Chorizite/index.json", "Chorizite", IndexBuilder.GithubToken, true, "basic");
        }

        internal async Task Build() {
            try {
                Directory.CreateDirectory(workDirectory);
                await GetReleases();
                await GetExistingReleaseInfo();
                await MirrorPackages();
            }
            catch (Exception e) {
                Log($"Error During build: {e.Message}");
            }
        }

        private async Task MirrorPackages() {
            FindPackageByIdResource resource = await nugetRepo.GetResourceAsync<FindPackageByIdResource>();

            nugetVersions = await resource.GetAllVersionsAsync(PackageId, nugetCache, _log, CancellationToken.None);
            await Task.WhenAll(releaseInfos.Select(r => MirrorPackage(r)));
        }

        private async Task MirrorPackage(ReleaseInfo r) {
            try {
                var existing = nugetVersions.FirstOrDefault(v => v.ToNormalizedString() == r.manifestVersion);
                if (existing != null) {
                    if (options.Verbose) Log($"Package {PackageId}-{r.manifestVersion} already exists: ({string.Join(", ", nugetVersions.Select(v => v.ToNormalizedString()))})");
                    return;
                }
                Log($"Package ({Name}:{IsCorePlugin}){PackageId}-{r.manifestVersion} needs mirroring");

                var nugetUrl = $"https://nuget.pkg.github.com/{repoOwner}/download/{PackageId}/{r.manifestVersion}/{PackageId}.{r.manifestVersion}.nupkg";
                var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{IndexBuilder.GithubUser}:{IndexBuilder.GithubToken}"));

                var nugetFile = Path.Combine(workDirectory, $"{PackageId}.{r.manifestVersion}.nupkg");

                using (var request = new HttpRequestMessage(HttpMethod.Get, nugetUrl)) {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

                    using (var response = await http.SendAsync(request)) {
                        if (!response.IsSuccessStatusCode) {
                            Log($"Error downloading nuget package ({PackageId}-{r.manifestVersion}): {response.StatusCode}");
                            return;
                        }
                        using (var fileStream = File.Create(nugetFile)) {
                            await response.Content.CopyToAsync(fileStream);
                        }
                    }
                }

                if (!File.Exists(nugetFile)) {
                    Log($"Error downloading nuget package ({PackageId}-{r.manifestVersion}): File not found");
                    return;
                }

                PackageUpdateResource resource = await nugetRepo.GetResourceAsync<PackageUpdateResource>();
                await resource.Push(
                    [nugetFile],
                    symbolSource: null,
                    timeoutInSecond: 5 * 60,
                    disableBuffering: false,
                    getApiKey: packageSource => IndexBuilder.GithubToken,
                    getSymbolApiKey: packageSource => null,
                    noServiceEndpoint: false,
                    skipDuplicate: true,
                    symbolPackageUpdateResource: null,
                    _log);
            }
            catch (Exception e) {
                Log($"Error mirroring package ({r.manifestName}-{r.manifestVersion}): {e.Message}");
            }
        }

        private async Task GetExistingReleaseInfo() {
            if (Latest is null) return;

            //if (options.Verbose) Log($"Downloading: ");
            using (var response = await http.GetAsync($"https://chorizite.github.io/Plugins/plugins/{Latest.manifestName}.json")) {
                if (!response.IsSuccessStatusCode) {
                    Log($"Error getting existing release info: {response.StatusCode}");
                    return;
                }

                using (var fileStream = File.Create(Path.Combine(workDirectory, $"{Latest.manifestName}.json"))) {
                    await response.Content.CopyToAsync(fileStream);
                }
            }
        }

        private async Task GetReleases() {
            try {
                var releases = await client.Repository.Release.GetAll(repoOwner, repoName);

                Log($"Found {releases.Count} releases.");
                await Task.WhenAll(releases.Select(r => GetRelease(r)));

                Releases = releaseInfos.ToList();
                Releases.Sort((a, b) => b.Date.CompareTo(a.Date));

                Latest = Releases.FirstOrDefault(r => !r.IsBeta);
                LatestBeta = Releases.FirstOrDefault(r => r.IsBeta);
                if (Latest is not null) {
                    Description = Latest.manifestDescription;
                }

                Author = Latest?.manifestAuthor ?? "";
            }

            catch (Exception e) {
                Log($"Error getting releases: {e.Message}");
            }
        }

        private async Task GetRelease(Release release) {
            try {
                var asset = await GetReleaseAsset(release);
                if (asset == null) {
                    Log($"Error: No zip found for release: {release.TagName} ({string.Join(", ", release.Assets.Select(a => a.Name))})");
                    return;
                }

                var releasePath = await DownloadAndExtractRelease(release, asset);
                if (string.IsNullOrEmpty(releasePath)) {
                    Log($"Error: No zip found for release: {release.TagName}");
                    return;
                }

                var manifestPath = FindManifestFile(releasePath);
                if (string.IsNullOrEmpty(manifestPath)) {
                    Log($"Error: No manifest found for release: {release.TagName}");
                    return;
                }

                var releaseInfo = await BuildReleaseInfo(release, asset, manifestPath);
                releaseInfos.Add(releaseInfo);
            }
            catch (Exception e) {
                Log($"Error getting release ({release.Name}): {e.Message}");
            }
        }

        private async Task<ReleaseInfo> BuildReleaseInfo(Release release, ReleaseAsset asset, string manifestPath) {
            var releaseInfo = new ReleaseInfo(this, release, asset, manifestPath);
            await releaseInfo.Build();
            return releaseInfo;
        }

        private async Task<ReleaseAsset?> GetReleaseAsset(Release release) {
            return release.Assets.FirstOrDefault(a => !a.Name.Contains("Source code") && a.Name.EndsWith(".zip"));
        }

        private async Task<string?> DownloadAndExtractRelease(Release release, ReleaseAsset zip) {
            try {
                var dirName = release.TagName.Split('/').Last();
                var zipPath = Path.Combine(workDirectory, $"{dirName}.zip");
                var extractPath = Path.Combine(workDirectory, $"{dirName}");

                if (File.Exists(zipPath)) File.Delete(zipPath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);

                if (options.Verbose) Log($"Downloading: {zip.Name}: {zip.BrowserDownloadUrl} -> {zipPath}");
                using (var response = await http.GetAsync(zip.BrowserDownloadUrl)) {
                    response.EnsureSuccessStatusCode();
                    using (var fileStream = File.Create(zipPath)) {
                        await response.Content.CopyToAsync(fileStream);
                    }
                }

                Directory.CreateDirectory(extractPath);
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractPath));
                if (options.Verbose) Log($"Extracted: {zip.Name}: {extractPath}");

                return extractPath;
            }
            catch (Exception e) {
                Log($"Error downloading release ({release.Name}): {e.Message}");
                return null;
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

        private void Log(string v) {
            Console.WriteLine($"[{repoPath}] {v}");
        }
    }

}