using Chorizite.PluginIndexBuilder.Models;
using Discord;
using Discord.Webhook;
using Octokit;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Security.Cryptography;
using SixLabors.ImageSharp.Drawing;
using Chorizite.Core.Plugins;
using DatReaderWriter.Types;
using NJsonSchema;
using NJsonSchema.Generation;

namespace Chorizite.PluginIndexBuilder {
    internal class IndexBuilder {
        private Options options;
        private List<RepositoryInfo> repositories = [];
        internal static JsonSerializerOptions jsonOpts = new JsonSerializerOptions {
            WriteIndented = true,
            IncludeFields = false,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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

                var repositories = await GetRepositories();
                var releaseModel = await BuildReleasesIndexModel(repositories);

                var indexJson = JsonSerializer.Serialize(releaseModel, jsonOpts);
                File.WriteAllText(System.IO.Path.Combine(options.OutputDirectory, "index.json"), indexJson);

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

                //await PostPluginUpdates(releaseResults);
                //await PostPluginReleaseAssetModifications(releaseResults);

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

            File.WriteAllText(System.IO.Path.Combine(options.OutputDirectory, "schemas", "release-index-schema.json"), indexSchemaJson);

            var pluginDetailsSchema = JsonSchema.FromType<PluginDetailsModel>(settings);
            var pluginDetailsSchemaJson = pluginDetailsSchema.ToJson(Newtonsoft.Json.Formatting.Indented);

            File.WriteAllText(System.IO.Path.Combine(options.OutputDirectory, "schemas", "plugin-details-schema.json"), pluginDetailsSchemaJson);
        }

        private async Task<PluginDetailsModel> BuildPluginDetailsModel(PluginListingModel plugin, RepositoryInfo repo) {
            var releases = new List<ReleaseDetailsModel>();
            foreach (var r in repo.Releases) {
                var model = new ReleaseDetailsModel() {
                    Name = r.Name,
                    DownloadUrl = r.DownloadUrl,
                    Sha256 = await CalculateSha256(r.zipPath),
                    Created = r.asset.CreatedAt.UtcDateTime,
                    Updated = r.asset.UpdatedAt.UtcDateTime,
                    Downloads = r.asset.DownloadCount,
                    IsBeta = r.IsBeta,
                    Dependencies = r.Manifest?["dependencies"]?.AsArray().Select(d => d.ToString()).ToList() ?? [],
                    Environments = r.Manifest?["environments"]?.AsArray().Select(d => d.ToString()).ToList() ?? [],
                    Changelog = r.Changelog,
                    Version = r.Version
                };

                releases.Add(model);
            }

            return new PluginDetailsModel() {
                Id = plugin.Id,
                Name = plugin.Name,
                Author = plugin.Author,
                Website = plugin.Website,
                Description = plugin.Description,
                IsDefault = plugin.IsDefault,
                IsOfficial = plugin.IsOfficial,
                TotalDownloads = plugin.TotalDownloads,
                Releases = releases
            };
        }

        private async Task<ReleasesIndexModel> BuildReleasesIndexModel(List<RepositoryInfo> repositories) {
            return new ReleasesIndexModel() {
                Chorizite = await BuildChoriziteReleaseModel(),
                Plugins = await BuildPluginListing(repositories.Where(r => r.Latest is not null))
            };
        }

        private async Task<List<PluginListingModel>> BuildPluginListing(IEnumerable<RepositoryInfo> repos) {
            var plugins = new List<PluginListingModel>();
            foreach (var r in repos) {
                plugins.Add(new PluginListingModel() {
                    Id = r.Id,
                    Name = r.Name,
                    Website = r.RepoUrl,
                    Author = r.Author,
                    IsDefault = r.IsCorePlugin,
                    IsOfficial = r.repoOwner == "Chorizite",
                    TotalDownloads = r.Releases.Sum(re => re.asset.DownloadCount),
                    Latest = new() {
                        Version = r.Latest!.Version,
                        DownloadUrl = r.Latest.DownloadUrl,
                        Sha256 = await CalculateSha256(r.Latest.zipPath),
                        Downloads = r.Releases.First(re => re.Version == r.Latest.Version).asset.DownloadCount,
                        Created = r.Latest.asset.CreatedAt.UtcDateTime,
                        Updated = r.Latest.asset.UpdatedAt.UtcDateTime,
                        HasReleaseModifications = false
                    },
                    LatestBeta = r.LatestBeta?.Version is not null && new Version(r.Latest.Version) <= new Version(r.LatestBeta.Version.Split('-').First()) ? new() {
                        Version = r.LatestBeta.Version,
                        DownloadUrl = r.LatestBeta.DownloadUrl,
                        Sha256 = await CalculateSha256(r.LatestBeta.zipPath),
                        Downloads = r.Releases.First(re => re.Version == r.LatestBeta.Version).asset.DownloadCount,
                        Created = r.LatestBeta.asset.CreatedAt.UtcDateTime,
                        Updated = r.LatestBeta.asset.UpdatedAt.UtcDateTime,
                        HasReleaseModifications = false
                    } : null,
                    Description = r.Description ?? ""
                });
            }
            return plugins;
        }

        private async Task<string> CalculateSha256(string filePath) {
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

        private async Task<List<RepositoryInfo>> GetRepositories() {
            var jsonString = File.ReadAllText(options.RespositoriesJsonPath);
            var json = JsonNode.Parse(jsonString);

            if (json?.AsObject().ContainsKey("repositories") != true) {
                throw new Exception($"Missing repositories key in {options.RespositoriesJsonPath}");
            }

            foreach (var repo in json["repositories"]!.AsObject()) {
                repositories.Add(new RepositoryInfo(repo.Key.ToString(), repo.Value!.ToString(), _client, options));
            }
            await Task.WhenAll(repositories.Select(r => r.Build()));

            return repositories.Where(r => r.Latest is not null).ToList();
        }

        private async Task PostPluginReleaseAssetModifications(IEnumerable<RepositoryInfo> releaseResults) {

            var assetChangeEmbeds = new List<EmbedBuilder>();
            foreach (var repo in releaseResults) {
                var assetChanges = new List<string>();
                foreach (var release in repo.Releases ?? []) {
                    if (release.IsNew) continue;

                    if (release.OldHash != release.Hash) {
                        assetChanges.Add($"Changed {release.Name} Asset ({release.OldHash} -> {release.Hash})");
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

        private async Task PostPluginUpdates(IEnumerable<RepositoryInfo> releaseResults) {
            try {
                var newReleaseEmbeds = new List<EmbedBuilder>();
                foreach (var repo in releaseResults) {
                    if (repo.Latest?.IsNew == true) {
                        newReleaseEmbeds.Add(new EmbedBuilder {
                            Title = $"{repo.Id} {repo.Latest.Name}",
                            Description = repo.Latest.Changelog,
                            Color = Color.Green,
                            Url = repo.RepoUrl,
                            Author = new EmbedAuthorBuilder {
                                Name = repo.Id,
                                IconUrl = $"https://chorizite.github.io/plugin-index/plugins/{repo.Id}.png",
                            }
                        });
                        Console.WriteLine($"New release: {repo.Id} {repo.Latest.Name}");
                    }
                    else if (repo.LatestBeta?.IsNew == true) {
                        newReleaseEmbeds.Add(new EmbedBuilder {
                            Title = $"[beta] {repo.Id} v{repo.LatestBeta.Name}",
                            Description = repo.LatestBeta.Changelog,
                            Color = Color.Teal,
                            Url = repo.RepoUrl,
                            Author = new EmbedAuthorBuilder {
                                Name = repo.Id,
                                IconUrl = $"https://chorizite.github.io/plugin-index/plugins/{repo.Id}.png",
                            }
                        });
                        Console.WriteLine($"New release: {repo.Id} (Beta) {repo.LatestBeta.Name}");
                    }
                }

                if (newReleaseEmbeds.Count > 0) {
                    await discord.SendMessageAsync(text: "New plugin releases!", embeds: newReleaseEmbeds.Select(e => e.Build()));
                }
            }
            catch (Exception e) {
                Console.WriteLine($"Error posting plugin updates: {e.Message}");
            }
        }

        private async Task<ChoriziteInfoModel> BuildChoriziteReleaseModel() {
            var allReleases = await _client.Repository.Release.GetAll("Chorizite", "Chorizite");
            var release = allReleases.First();
            var asset = release.Assets.FirstOrDefault(a => !a.Name.Contains("Source code") && a.Name.Contains("Installer"));

            if (asset == null) {
                throw new Exception($"Error: No zip found for release: {release.TagName} ({string.Join(", ", release.Assets.Select(a => a.Name))})");
            }

            return new ChoriziteInfoModel() {
                Latest = new() {
                    Version = release.TagName.Split('/').Last(),
                    DownloadUrl = asset.BrowserDownloadUrl,
                    Sha256 = await CalculateSha256(asset.BrowserDownloadUrl),
                    Downloads = asset.DownloadCount,
                    Created = asset.CreatedAt.UtcDateTime,
                    Updated = asset.UpdatedAt.UtcDateTime,
                    HasReleaseModifications = false
                },
                LatestBeta = null
            };
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