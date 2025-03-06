using Discord;
using Discord.Webhook;
using Octokit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Security.Cryptography;
using NJsonSchema;
using NJsonSchema.Generation;
using Chorizite.Plugins.Models;
using System.Net.Http;

namespace Chorizite.PluginIndexBuilder {
    internal class IndexBuilder {
        private Options options;
        private List<RepoInfo> repositories = [];

        internal static List<string> DefaultPluginIds = ["Lua", "RmlUi", "Launcher", "AC", "PluginManagerUI"];
        internal static JsonSerializerOptions jsonOpts = new JsonSerializerOptions {
            WriteIndented = true,
            IncludeFields = false,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        internal static string? GithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        internal static string? GithubUser = Environment.GetEnvironmentVariable("GITHUB_USER") ?? Environment.GetEnvironmentVariable("GH_USER");
        internal static string? DiscordWebhook = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK");

        private GitHubClient _client;
        private DiscordWebhookClient discord;

        public IndexBuilder(Options opts) {
            options = opts;

            if (string.IsNullOrEmpty(GithubToken)) {
                throw new Exception("Missing GITHUB_TOKEN environment variable");
            }
            if (string.IsNullOrEmpty(GithubUser)) {
                throw new Exception("Missing GITHUB_USER environment variable");
            }
            if (string.IsNullOrEmpty(DiscordWebhook)) {
                throw new Exception("Missing DISCORD_WEBHOOK environment variable");
            }

            discord = new DiscordWebhookClient(DiscordWebhook);

            _client = new GitHubClient(new ProductHeaderValue("Chorizite.PluginIndexBuilder"));
            var tokenAuth = new Credentials(GithubToken);
            _client.Credentials = tokenAuth;
        }

        internal async Task Build() {
            try {
                MakeDirectories();
                MakeSchemas();

                var repositories = (await GetRepositories()).Where(r => r.IsValid).ToList();
                var choriziteReleases = await BuildChoriziteReleaseModel();
                var releaseModel = await BuildReleasesIndexModel(repositories, choriziteReleases);

                var indexJson = JsonSerializer.Serialize(releaseModel, jsonOpts);

                // the json plugin that the installer uses barfs on empty arrays / objects
                // so we have to make sure to null them lol
                indexJson = indexJson.Replace("[]", "null").Replace("{}", "null");
                
                File.WriteAllText(System.IO.Path.Combine(options.OutputDirectory, "index.json"), indexJson);

                var choriziteReleasesJson = JsonSerializer.Serialize(new ChoriziteDetailsModel() {
                    TotalDownloads = choriziteReleases.Sum(r => r.Downloads),
                    Releases = choriziteReleases
                }, jsonOpts);
                File.WriteAllText(System.IO.Path.Combine(options.OutputDirectory, "chorizite.json"), choriziteReleasesJson);

                List<PluginDetailsModel> detailsModels = [];
                foreach (var plugin in releaseModel.Plugins) {
                    var repo = repositories.First(r => r.Id == plugin.Id);
                    PluginDetailsModel details = await BuildPluginDetailsModel(plugin, repo);

                    var repoJsonPath = System.IO.Path.Combine(options.OutputDirectory, "plugins", $"{plugin.Id}.json");
                    var repoJson = JsonSerializer.Serialize(details, jsonOpts);

                    File.WriteAllText(repoJsonPath, repoJson);
                    File.Copy(System.IO.Path.Combine(repo.workDirectory, "icon.png"), System.IO.Path.Combine(options.OutputDirectory, "plugins", $"{plugin.Id}.png"));

                    detailsModels.Add(details);
                }

                BuildHtml(releaseModel.Chorizite, detailsModels, System.IO.Path.Combine(options.OutputDirectory, "index.html"));

                //await PostChoriziteUpdates(choriziteReleases);
                //await PostPluginUpdates(repositories);
                //await PostPluginReleaseAssetModifications(repositories);

                discord.Dispose();
            }
            catch (Exception e) {
                Console.WriteLine($"Error building index: {e}");
            }
        }

        private void MakeSchemas() {
            Directory.CreateDirectory(System.IO.Path.Combine(options.OutputDirectory, "schemas"));
            var settings = new SystemTextJsonSchemaGeneratorSettings() {
                SerializerOptions = jsonOpts
            };

            var indexSchema = JsonSchema.FromType<ReleasesIndexModel>(settings);
            var indexSchemaJson = indexSchema.ToJson(Newtonsoft.Json.Formatting.Indented);

            File.WriteAllText(System.IO.Path.Combine(options.OutputDirectory, "schemas", "release-index.json"), indexSchemaJson);

            var choriziteReleasesSchema = JsonSchema.FromType<ChoriziteDetailsModel>(settings);
            var choriziteReleasesSchemaJson = choriziteReleasesSchema.ToJson(Newtonsoft.Json.Formatting.Indented);

            File.WriteAllText(System.IO.Path.Combine(options.OutputDirectory, "schemas", "chorizite-details.json"), choriziteReleasesSchemaJson);

            var pluginDetailsSchema = JsonSchema.FromType<PluginDetailsModel>(settings);
            var pluginDetailsSchemaJson = pluginDetailsSchema.ToJson(Newtonsoft.Json.Formatting.Indented);

            File.WriteAllText(System.IO.Path.Combine(options.OutputDirectory, "schemas", "plugin-details-schema.json"), pluginDetailsSchemaJson);

            File.Copy(Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location)!, "schemas", "plugin-manifest.json"), Path.Combine(options.OutputDirectory, "schemas", "plugin-manifest.json"));
        }

        private async Task<PluginDetailsModel> BuildPluginDetailsModel(PluginListingModel plugin, RepoInfo repo) {
            var releases = new List<ReleaseDetailsModel>();

            return new PluginDetailsModel() {
                Id = plugin.Id,
                Name = plugin.Name,
                Author = plugin.Author,
                Website = plugin.Website,
                Description = plugin.Description,
                Dependencies = plugin.Latest.Dependencies?.Count > 0 ? plugin.Latest.Dependencies : null,
                Environments = BuildEnvironments(plugin.Latest.Environments),
                IsDefault = plugin.IsDefault,
                IsOfficial = plugin.IsOfficial,
                TotalDownloads = plugin.TotalDownloads,
                Releases = repo.Releases.Select(r => r.Details).Where(r => r is not null).Cast<ReleaseDetailsModel>().ToList()
            };
        }

        private async Task<ReleasesIndexModel> BuildReleasesIndexModel(List<RepoInfo> repositories, List<ReleaseDetailsModel> choriziteReleases) {
            var latest = choriziteReleases.First(r => r.IsBeta == false);
            //var latestBeta = choriziteReleases.First(r => r.IsBeta == true);
            return new ReleasesIndexModel() {
                Chorizite = new ChoriziteInfoModel() {
                    Latest = new ReleaseModel() {
                        Created = latest.Created,
                        Sha256 = latest.Sha256,
                        DownloadUrl = latest.DownloadUrl,
                        Version = latest.Version,
                        Downloads = latest.Downloads,
                        Updated = latest.Updated,
                        Dependencies = latest.Dependencies?.Count > 0 ? latest.Dependencies : null,
                        Environments = BuildEnvironments(latest.Environments),
                        HasReleaseModifications = false
                    },
                    LatestBeta = null
                },
                Plugins = await BuildPluginListing(repositories)
            };
        }

        private async Task<List<PluginListingModel>> BuildPluginListing(IEnumerable<RepoInfo> repos) {
            var plugins = new List<PluginListingModel>();
            foreach (var r in repos) {
                plugins.Add(new PluginListingModel() {
                    Id = r.Id,
                    Name = r.Name,
                    Website = r.RepoUrl,
                    Author = r.Author,
                    IsDefault = DefaultPluginIds.Contains(r.Id),
                    IsOfficial = r._repoOwner == "Chorizite",
                    Dependencies = r.Latest.Details.Dependencies,
                    Environments = BuildEnvironments(r.Latest.Details.Environments),
                    TotalDownloads = r.Releases.Sum(r => r.Details?.Downloads ?? 0),
                    Latest = BuildPluginReleaseModel(r.Latest),
                    LatestBeta = BuildPluginReleaseModel(r.LatestBeta),
                    Description = r.Description ?? ""
                });
            }
            return plugins;
        }

        private List<string>? BuildEnvironments(List<string>? environments) {
            if (environments is null) return null;
            if (environments.Count == 0) return null;
            if (environments.Count == 1 && environments[0] == "DocGen") return new List<string>() { "Client", "Launcher" };

            return environments;
        }

        private ReleaseModel? BuildPluginReleaseModel(ReleaseInfo? latest) {
            if (latest?.Details is null) return null;

            return new() {
                Version = latest.Details.Version,
                DownloadUrl = latest.Details.DownloadUrl,
                Sha256 = latest.Details.Sha256,
                Downloads = latest.Details.Downloads,
                Environments = BuildEnvironments(latest.Details.Environments),
                Dependencies = latest.Details.Dependencies,
                Created = latest.Details.Created,
                Updated = latest.Details.Updated,
                HasReleaseModifications = false
            };
        }

        internal static async Task<string> CalculateSha256(string filePath) {
            Stream stream;
            if (filePath.StartsWith("http")) {
                stream = await Program.http.GetStreamAsync(filePath);
            }
            else {
                stream = File.OpenRead(filePath);
            }
            using (SHA256 hashAlgorithm = SHA256.Create()) {
                var hashedByteArray = hashAlgorithm.ComputeHash(stream);
                stream.Dispose();
                return BitConverter.ToString(hashedByteArray).Replace("-", String.Empty);
            }
        }

        private async Task<List<RepoInfo>> GetRepositories() {
            var jsonString = File.ReadAllText(options.RespositoriesJsonPath);
            var json = JsonNode.Parse(jsonString);

            if (json?.AsObject().ContainsKey("repositories") != true) {
                throw new Exception($"Missing repositories key in {options.RespositoriesJsonPath}");
            }

            foreach (var repo in json["repositories"]!.AsObject()) {
                repositories.Add(new RepoInfo(repo.Key.ToString(), repo.Value!.ToString(), _client, options));
            }
            await Task.WhenAll(repositories.Select(r => r.Build()));

            return repositories.Where(r => r.Latest is not null).ToList();
        }

        private async Task PostPluginReleaseAssetModifications(IEnumerable<RepoInfo> releaseResults) {
            var assetChangeEmbeds = new List<EmbedBuilder>();
            foreach (var repo in releaseResults) {
                var assetChanges = new List<string>();
                foreach (var release in repo.Releases ?? []) {
                    if (release.IsNew) continue;

                    if (release.HasAssetModifications) {
                        assetChanges.Add($"Changed {repo.Id}({repo.Name} {release.Details?.Version}) asset");
                    }
                }
                if (assetChanges.Count > 0) {
                    assetChangeEmbeds.Add(new EmbedBuilder {
                        Title = $"{repo.Id}",
                        Description = string.Join("\n", assetChanges),
                        Color = Color.Red,
                        Url = repo.RepoUrl,
                        Author = new EmbedAuthorBuilder {
                            Name = repo.Id,
                            IconUrl = $"https://chorizite.github.io/plugin-index/plugins/{repo.Id}.png",
                        }
                    });
                }
            }

            if (assetChangeEmbeds.Count > 0) {
                await discord.SendMessageAsync(text: ":warning: Plugin release assets changed! :warning:", embeds: assetChangeEmbeds.Select(e => e.Build()));
            }
        }

        private async Task PostChoriziteUpdates(List<ReleaseDetailsModel> choriziteReleases) {
            var json = await Program.http.GetStringAsync("https://chorizite.github.io/plugin-index/chorizite.json");
            var existingReleases = JsonSerializer.Deserialize<ChoriziteDetailsModel>(json, jsonOpts);

            var latestExisting = existingReleases.Releases?.FirstOrDefault();
            var latest = choriziteReleases.FirstOrDefault();

            if (latest is null) return;

            Console.WriteLine($"Latest Chorizite version: {latest.Version} (Old: {latestExisting?.Version})");

            if (new Version(latestExisting?.Version ?? "0.0.0") < new Version(latest.Version)) {
                var eb = new EmbedBuilder {
                    Title = $"Download Chorizite v{latest.Version} Installer",
                    Description = FormatChangelog(latest.Changelog),
                    Color = Color.Purple,
                    Url = latest.DownloadUrl,
                    Author = new EmbedAuthorBuilder {
                        Name = "Chorizite",
                        IconUrl = $"https://avatars.githubusercontent.com/u/184878336?s=200",
                    }
                };
                await discord.SendMessageAsync(text: ":fire: New Chorizite release! :fire:", embeds: [ eb.Build() ]);
            }
        }

        private async Task PostPluginUpdates(IEnumerable<RepoInfo> releaseResults) {
            try {
                var newReleaseEmbeds = new List<EmbedBuilder>();
                foreach (var repo in releaseResults) {
                    if (repo.Latest?.IsNew == true) {
                        newReleaseEmbeds.Add(new EmbedBuilder {
                            Title = $"{repo.Name} v{repo.Latest.Details.Version}",
                            Description = FormatChangelog(repo.Latest.Details.Changelog),
                            Color = Color.Green,
                            Url = repo.RepoUrl,
                            Author = new EmbedAuthorBuilder {
                                Name = repo.Id,
                                IconUrl = $"https://chorizite.github.io/plugin-index/plugins/{repo.Id}.png",
                            }
                        });
                        Console.WriteLine($"New release: {repo.Id} {repo.Latest.Details.Name}");
                    }
                    else if (repo.LatestBeta?.IsNew == true) {
                        newReleaseEmbeds.Add(new EmbedBuilder {
                            Title = $"[beta] {repo.Name} v{repo.LatestBeta.Details.Version}",
                            Description = FormatChangelog(repo.LatestBeta.Details.Changelog),
                            Color = Color.Teal,
                            Url = repo.RepoUrl,
                            Author = new EmbedAuthorBuilder {
                                Name = repo.Id,
                                IconUrl = $"https://chorizite.github.io/plugin-index/plugins/{repo.Id}.png",
                            }
                        });
                        Console.WriteLine($"New release: {repo.Id} (Beta) {repo.LatestBeta.Details.Name}");
                    }
                }

                if (newReleaseEmbeds.Count > 0) {
                    await discord.SendMessageAsync(text: ":fire: New plugin releases! :fire:", embeds: newReleaseEmbeds.Select(e => e.Build()));
                }
            }
            catch (Exception e) {
                Console.WriteLine($"Error posting plugin updates: {e.Message}");
            }
        }

        private string FormatChangelog(string changelog) {
            return changelog
                .Replace("## What's Changed", "### What's Changed")
                .Replace("## New Contributors", "### New Contributors");
        }

        private async Task<List<ReleaseDetailsModel>> BuildChoriziteReleaseModel() {
            var allReleases = await _client.Repository.Release.GetAll("Chorizite", "Chorizite");
            var releases = new List<ReleaseDetailsModel>();

            foreach (var release in allReleases) {
                var asset = release.Assets.FirstOrDefault(a => !a.Name.Contains("Source code") && a.Name.Contains("Installer"));

                if (asset == null) {
                    throw new Exception($"Error: No zip found for release: {release.TagName} ({string.Join(", ", release.Assets.Select(a => a.Name))})");
                }

                releases.Add(new() {
                    Name = release.Name,
                    Changelog = release.Body,
                    IsBeta = release.TagName.Split('/').Last().Contains("-"),
                    Version = release.TagName.Split('/').Last(),
                    DownloadUrl = asset.BrowserDownloadUrl,
                    Sha256 = await CalculateSha256(asset.BrowserDownloadUrl),
                    Downloads = asset.DownloadCount,
                    Created = asset.CreatedAt.UtcDateTime,
                    Updated = asset.UpdatedAt.UtcDateTime,
                    Dependencies = [],
                    Environments = [],
                    HasReleaseModifications = false
                });
            }

            return releases;
        }

        private void MakeDirectories() {
            if (Directory.Exists(options.OutputDirectory)) {
                Directory.Delete(options.OutputDirectory, true);
            }

            if (Directory.Exists(options.WorkDirectory)) {
                Directory.Delete(options.WorkDirectory, true);
            }

            Directory.CreateDirectory(options.OutputDirectory);
            Directory.CreateDirectory(options.WorkDirectory);
            Directory.CreateDirectory(System.IO.Path.Combine(options.OutputDirectory, "plugins"));
        }

        private void BuildHtml(ChoriziteInfoModel choriziteRelease, IEnumerable<PluginDetailsModel> plugins, string outFile) {
            var builder = new HtmlBuilder(choriziteRelease, plugins);
            var output = builder.BuildHtml();
            File.WriteAllText(outFile, output);
        }
    }
}