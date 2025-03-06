using Chorizite.Plugins.Models;
using Discord;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Octokit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Configuration;
using NuGet.Protocol;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;

namespace Chorizite.PluginIndexBuilder {
    public class RepoInfo {
        private GitHubClient _githubClient;
        private Options _options;
        private Logger _log;
        private SourceCacheContext _nugetCache;
        private SourceRepository _nugetRepo;
        private IEnumerable<NuGetVersion> nugetVersions;

        internal string _repoPath => RepoUrl.Replace("https://github.com/", "");
        internal string _repoOwner => _repoPath.Split("/")[0];
        internal string _repoName => _repoPath.Split("/")[1];
        internal string workDirectory => Path.Combine(_options.WorkDirectory, _repoOwner, _repoName);

        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public string RepoUrl { get; private set; }
        public string Author { get; private set; }

        public bool IsValid => Latest is not null;

        public ReleaseInfo? Latest => Releases.FirstOrDefault(r => r.IsValid && r.Details?.IsBeta == false);
        public ReleaseInfo? LatestBeta => Releases.FirstOrDefault(r => r.IsValid && r.Details?.IsBeta == true);

        public List<ReleaseInfo> Releases { get; private set; } = [];
        public PluginDetailsModel? ExistingReleaseInfo { get; private set; }
        public bool IsDefault => IndexBuilder.DefaultPluginIds.Contains(Id);
        public string PackageId => IsDefault ? $"Chorizite.Plugins.{Id}" : Id;

        public RepoInfo(string id, string repoUrl, GitHubClient client, Options options) {
            Id = id;
            RepoUrl = repoUrl;
            _githubClient = client;
            _options = options;

            try {
                Directory.Delete(workDirectory, true);
            }
            catch { }

            _log = new Logger(id, options);

            _nugetCache = new SourceCacheContext();
            _nugetCache.NoCache = true;
            _nugetRepo = NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3($"https://nuget.pkg.github.com/Chorizite/index.json");
            _nugetRepo.PackageSource.Credentials = new PackageSourceCredential($"https://nuget.pkg.github.com/Chorizite/index.json", "Chorizite", IndexBuilder.GithubToken, true, "basic");
        }

        internal async Task Build() {
            try {
                Directory.CreateDirectory(workDirectory);
                ExistingReleaseInfo = await GetExistingReleaseInfo();
                Releases = (await GetReleases()).Where(r => r.IsValid).ToList();
                Author = Releases.FirstOrDefault(r => r.IsNew)?.Manifest?.Author ?? ExistingReleaseInfo?.Author ?? _repoOwner;
                Name = Releases.FirstOrDefault(r => r.IsNew)?.Manifest?.Name ?? ExistingReleaseInfo?.Name ?? _repoName;
                Description = Releases.FirstOrDefault(r => r.IsNew)?.Manifest?.Description ?? ExistingReleaseInfo?.Description ?? "";

                await MirrorPackages();
                await CopyIcon();
            }
            catch (Exception e) {
                Log($"Error During build: {e}");
            }
        }

        private async Task<PluginDetailsModel?> GetExistingReleaseInfo() {
            try {
                using (var response = await Program.http.GetAsync($"https://chorizite.github.io/plugin-index/plugins/{Id}.json")) {
                    if (!response.IsSuccessStatusCode) {
                        Log($"Error getting existing release info: {response.StatusCode}");
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<PluginDetailsModel>(json, IndexBuilder.jsonOpts);
                }
            }
            catch (Exception e) {
                Log($"Error getting existing release info: {e.Message}");
            }
            return null;
        }

        private async Task<List<ReleaseInfo>> GetReleases() {
            try {
                var releases = await _githubClient.Repository.Release.GetAll(_repoOwner, _repoName);

                Log($"Found {releases.Count} releases.");
                var rels = new List<ReleaseInfo?>();
                foreach (var release in releases) {
                    rels.Add(await GetRelease(release));
                }

                return rels.Where(r => r is not null).Cast<ReleaseInfo>().ToList();
            }

            catch (Exception e) {
                Log($"Error getting releases: {e.Message}");
            }

            return [];
        }

        private async Task<ReleaseInfo?> GetRelease(Release release) {
            try {
                var existing = ExistingReleaseInfo?.Releases.FirstOrDefault(r => r.Version == release.TagName.Split('/').Last());
                ReleaseInfo relInfo = new(this, release, existing);

                await relInfo.Build();

                return relInfo;
            }
            catch (Exception e) {
                Log($"Error getting release ({release.Name}): {e.Message}");
            }

            return null;
        }

        private async Task CopyIcon() {
            if (Latest?.IsNew == true) {
                var icon = Latest.Manifest?.Icon;

                if (string.IsNullOrWhiteSpace(icon) || !File.Exists(Path.Combine(Latest.Manifest.BaseDirectory, icon))) {
                    using var image = SixLabors.ImageSharp.Image.Load(Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location), "default-icon.png"));
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
                            img.DrawText(text, font, SixLabors.ImageSharp.Color.Magenta, new PointF(image.Width / 2 - rect.Width / 2, image.Height / 2 - rect.Height / 2));
                        });
                    }
                    image.SaveAsPng(Path.Combine(workDirectory, "icon.png"));
                }
                else {
                    var iconPath = Path.Combine(Latest.Manifest.BaseDirectory, icon);
                    using var image = SixLabors.ImageSharp.Image.Load(iconPath);

                    image.SaveAsPng(Path.Combine(workDirectory, "icon.png"));
                }
            }
            else {
                // try to download existing image
                try {
                    var bytes = await Program.http.GetByteArrayAsync($"https://chorizite.github.io/plugin-index/plugins/{Id}.png");
                    File.WriteAllBytes(Path.Combine(workDirectory, "icon.png"), bytes);
                }
                catch (Exception e) {
                    Log($"Error downloading existing icon: {e.Message}");
                }
            }
        }

        private async Task MirrorPackages() {
            FindPackageByIdResource resource = await _nugetRepo.GetResourceAsync<FindPackageByIdResource>();

            nugetVersions = await resource.GetAllVersionsAsync(PackageId, _nugetCache, _log, CancellationToken.None);
            await Task.WhenAll(Releases.Where(r => r.IsNew).Select(r => MirrorPackage(r)));
        }

        private async Task MirrorPackage(ReleaseInfo r) {
            try {
                var existing = nugetVersions.FirstOrDefault(v => v.ToNormalizedString() == r.Details?.Version);
                if (existing != null) {
                    if (_options.Verbose) Log($"Package {PackageId}-{r.Details?.Version} already exists: ({string.Join(", ", nugetVersions.Select(v => v.ToNormalizedString()))})");
                    return;
                }
                Log($"Package ({Id}:{IsDefault}){PackageId}-{r.Details?.Version} needs mirroring");

                var nugetUrl = $"https://nuget.pkg.github.com/{_repoOwner}/download/{PackageId}/{r.Details?.Version}/{PackageId}.{r.Details?.Version}.nupkg";
                var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{IndexBuilder.GithubUser}:{IndexBuilder.GithubToken}"));

                var nugetFile = Path.Combine(workDirectory, $"{PackageId}.{r.Details?.Version}.nupkg");

                using (var request = new HttpRequestMessage(HttpMethod.Get, nugetUrl)) {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

                    using (var response = await Program.http.SendAsync(request)) {
                        if (!response.IsSuccessStatusCode) {
                            Log($"Error downloading nuget package ({PackageId}-{r.Details?.Version}): {response.StatusCode}");
                            return;
                        }
                        using (var fileStream = File.Create(nugetFile)) {
                            await response.Content.CopyToAsync(fileStream);
                        }
                    }
                }

                if (!File.Exists(nugetFile)) {
                    Log($"Error downloading nuget package ({PackageId}-{r.Details?.Version}): File not found");
                    return;
                }

                PackageUpdateResource resource = await _nugetRepo.GetResourceAsync<PackageUpdateResource>();
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
                Log($"Error mirroring package ({PackageId}-{r.Details?.Version}): {e.Message}");
            }
        }

        internal void Log(string v) {
            Console.WriteLine($"[{_repoPath}] {v}");
        }
    }
}
