using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Octokit;
using Octokit.Internal;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Chorizite.PluginIndexBuilder {
    public class RepositoryInfo {

        internal Options options;
        internal GitHubClient client;
        private Logger _log;

        private static List<string> CorePlugins = ["Lua", "RmlUi", "Launcher", "AC", "PluginManagerUI"];

        internal string repoPath => RepoUrl.Replace("https://github.com/", "");
        internal string repoOwner => repoPath.Split("/")[0];
        internal string repoName => repoPath.Split("/")[1];
        internal string workDirectory => Path.Combine(options.WorkDirectory, repoOwner, repoName);
        internal ConcurrentBag<ReleaseInfo> releaseInfos = new ConcurrentBag<ReleaseInfo>();

        public string Id { get; set; }
        public string Name => Latest?.manifestName ?? "";
        public string PackageId => IsCorePlugin ? $"Chorizite.Plugins.{Id}" : Id ;
        public bool IsCorePlugin => CorePlugins.Contains(Id);

        private SourceCacheContext nugetCache;

        public string Description { get; set; }
        public string RepoUrl { get; set; }
        public ReleaseInfo? Latest { get; set; }
        public ReleaseInfo? LatestBeta { get; set; }
        public string Author { get; set; }

        [JsonIgnore]
        public RepositoryInfo? ExistingReleaseInfo { get; private set; }

        public List<ReleaseInfo> Releases { get; set; } = [];
        private IReadOnlyList<PackageVersion>? _versions;
        private IEnumerable<NuGetVersion> nugetVersions;
        private SourceRepository nugetRepo;

        public RepositoryInfo() { }

        public RepositoryInfo(string id, string repoUrl, Octokit.GitHubClient client, Options options) {
            this.RepoUrl = repoUrl;
            this.options = options;
            this.client = client;
            _log = new Logger(id);

            Id = id;

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
                if (Latest is not null) {
                    await MirrorPackages();
                    await CopyIcon();
                }
            }
            catch (Exception e) {
                Log($"Error During build: {e}");
            }
        }

        private async Task<ReleaseAsset?> GetReleaseAsset(Release release) {
            return release.Assets.FirstOrDefault(a => !a.Name.Contains("Source code") && a.Name.EndsWith(".zip"));
        }

        private async Task CopyIcon() {
            var icon = Latest.Manifest["icon"]?.ToString();

            if (string.IsNullOrWhiteSpace(icon) || !File.Exists(Path.Combine(Path.GetDirectoryName(Latest.manifestPath), icon))) {
                using var image = Image.Load(Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location), "default-icon.png"));
                FontCollection collection = new();
                collection.Add(Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location), "Fonts", "Roboto.ttf"));

                if (collection.TryGet("Roboto", out FontFamily family)) {
                    Font font = family.CreateFont(62, FontStyle.Bold);
                    var text = Id.First().ToString();
                    var options = new TextOptions(font) {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    FontRectangle rect = TextMeasurer.MeasureAdvance(text, options);
                    image.Mutate(img => {
                        img.DrawText(text, font, Color.Magenta, new PointF(image.Width / 2 - rect.Width / 2, image.Height / 2 - rect.Height / 2));
                    });
                }
                image.SaveAsPng(Path.Combine(workDirectory, "icon.png"));
            }
            else {
                var iconPath = Path.Combine(Path.GetDirectoryName(Latest.manifestPath), icon);
                using var image = Image.Load(iconPath);

                image.SaveAsPng(Path.Combine(workDirectory, "icon.png"));
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
                Log($"Package ({Id}:{IsCorePlugin}){PackageId}-{r.manifestVersion} needs mirroring");

                var nugetUrl = $"https://nuget.pkg.github.com/{repoOwner}/download/{PackageId}/{r.manifestVersion}/{PackageId}.{r.manifestVersion}.nupkg";
                var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{IndexBuilder.GithubUser}:{IndexBuilder.GithubToken}"));

                var nugetFile = Path.Combine(workDirectory, $"{PackageId}.{r.manifestVersion}.nupkg");

                using (var request = new HttpRequestMessage(HttpMethod.Get, nugetUrl)) {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

                    using (var response = await Program.http.SendAsync(request)) {
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
            try {
                return;
                if (Latest is null) return;

                //if (options.Verbose) Log($"Downloading: ");
                using (var response = await Program.http.GetAsync($"https://chorizite.github.io/plugin-index/plugins/{Latest.manifestName}.json")) {
                    if (!response.IsSuccessStatusCode) {
                        Log($"Error getting existing release info: {response.StatusCode}");
                        return;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    ExistingReleaseInfo = JsonSerializer.Deserialize<RepositoryInfo>(json);
                    Console.WriteLine($"Existing release info: {ExistingReleaseInfo.Latest.Name}: ({string.Join(", ", ExistingReleaseInfo.Releases.Select(r => r.Version))})");
                }
            }
            catch (Exception e) {
                Log($"Error getting existing release info: {e.Message}");
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

                var releaseInfo = await BuildReleaseInfo(release, asset);
                releaseInfos.Add(releaseInfo);
            }
            catch (Exception e) {
                Log($"Error getting release ({release.Name}): {e.Message}");
            }
        }

        private async Task<ReleaseInfo> BuildReleaseInfo(Release release, ReleaseAsset asset) {
            var releaseInfo = new ReleaseInfo(this, release, asset);
            await releaseInfo.Build();
            return releaseInfo;
        }

        internal void Log(string v) {
            Console.WriteLine($"[{repoPath}] {v}");
        }
    }

}