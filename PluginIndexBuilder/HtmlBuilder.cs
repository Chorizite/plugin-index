using Chorizite.ACProtocol.Types;
using Chorizite.Plugins.Models;
using NuGet.Protocol.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Chorizite.PluginIndexBuilder {
    internal class HtmlBuilder {
        private ChoriziteInfoModel choriziteRelease;
        private IEnumerable<PluginDetailsModel> plugins;

        public HtmlBuilder(ChoriziteInfoModel choriziteRelease, IEnumerable<PluginDetailsModel> plugins) {
            this.choriziteRelease = choriziteRelease;
            this.plugins = plugins;
        }

        internal string? BuildHtml() {
            var officialPluginHtml = new StringBuilder();
            var communityPluginHtml = new StringBuilder();

            var officialPlugins = plugins.Where(p => p.IsOfficial == true).ToList();
            var communityPlugins = plugins.Where(p => p.IsOfficial == false).ToList();

            officialPlugins.Sort((p1, p2) => p1.Name.CompareTo(p2.Name));
            communityPlugins.Sort((p1, p2) => p1.Releases.First().Version.CompareTo(p2.Releases.First().Version));

            foreach (var plugin in officialPlugins) {
                officialPluginHtml.AppendLine(GetPluginHtml(plugin));
            }

            foreach (var plugin in communityPlugins) {
                communityPluginHtml.AppendLine(GetPluginHtml(plugin));
            }

            var html = $"""
<html>
    <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
       <title>Chorizite Plugin Index</title>
       <style>
        {GetStyles()}
       </style>
    </head>
    <body>
        <div class="container">
            <header class="header">
                <h1 class="header-title">Chorizite Plugin Index</h1>
                <div class="header-info">
                    <p>Latest Chorizite Release: </p>
                    <a href="https://github.com/Chorizite/Chorizite/releases/latest" target="_blank" class="release-tag">
                        {choriziteRelease.Latest.Version}
                    </a>
                    <a href="index.json" target="_blank">Releases API</a> |
                    <a href="chorizite.json" target="_blank">Chorizite Releases API</a>
                </div>
            </header>
            <div class="plugin-list">
                <h2>Official Plugins</h2>
                {officialPluginHtml}
                <h2>Community Plugins</h2>
                {communityPluginHtml}
            </div>
        </div>
    </body>
</html>
""";

            return html;
        }

        private string? GetPluginHtml(PluginDetailsModel plugin) {
            var body = new StringBuilder();
            var release = plugin.Releases.First();

            var deps = "";
            if (plugin.Dependencies?.Count > 0) {
                deps = $"""
                            <div class="plugin-meta-item">
                                <span class="plugin-meta-label">Depends On:</span>
                                <span>{string.Join(", ", plugin.Dependencies)}</span>
                            </div>
                    """;
            }
            body.AppendLine($"""
                <div class="plugin-card{(plugin.IsOfficial ? " official" : "")}">
                    <div class="plugin-header">
                        <img src="./plugins/{plugin.Id}.png" alt="{plugin.Name}" class="plugin-icon">
                        <div class="plugin-header-meta">
                            <h3 class="plugin-title">{plugin.Name}</h3>
                            <div class="plugin-meta-item">
                                <span class="plugin-meta-label">Author:</span>
                                <span>{plugin.Author}</span>
                            </div>
                        </div>
                    </div>

                    <div class="plugin-meta">
                        <div class="plugin-meta-item">
                            <span class="plugin-meta-label">Version:</span>
                            <span>{release.Version}</span>
                        </div>
                        <div class="plugin-meta-item">
                            <span class="plugin-meta-label">Updated:</span>
                            <span>{release.Updated.ToString("yyyy-MM-dd")}</span>
                        </div>
                        <div class="plugin-meta-item">
                            <span class="plugin-meta-label">Environment:</span>
                            <span>{string.Join(", ", plugin.Environments)}</span>
                        </div>
                        {deps}
                    </div>

                    <div class="plugin-description">
                        {plugin.Description}
                    </div>

                    <div class="plugin-links">
                        <a href="{plugin.Website}" target="_blank">
                            View Website
                        </a>
                        <a href="./plugins/{plugin.Id}.json" target="_blank">
                            Plugin Releases API
                        </a>
                        <a href="{release.DownloadUrl}" class="plugin-download">
                            Download
                        </a>
                    </div>
                </div>
                """);

            return body.ToString();
        }

        public string GetStyles() {
            return """
                :root {
                    --primary-color: #2c3e50;
                    --secondary-color: #3498db;
                    --background-light: #f4f4f4;
                    --text-color: #333;
                    --card-background: #ffffff;
                    --border-color: #e0e0e0;
                }

                * {
                    margin: 0;
                    padding: 0;
                    box-sizing: border-box;
                }

                body {
                    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, Cantarell, 'Open Sans', 'Helvetica Neue', sans-serif;
                    line-height: 1.6;
                    color: var(--text-color);
                    background-color: var(--background-light);
                }

                .container {
                    width: 100%;
                    max-width: 800px;
                    margin: 0 auto;
                    padding: 1rem;
                }

                .header {
                    margin-bottom: 2rem;
                    text-align: center;
                }

                .header-title {
                    font-size: 2.5rem;
                    color: var(--primary-color);
                    margin-bottom: 1rem;
                }

                .header-info {
                    display: flex;
                    justify-content: center;
                    align-items: center;
                    gap: 1rem;
                    flex-wrap: wrap;
                }

                .release-tag {
                    background-color: var(--secondary-color);
                    color: white;
                    padding: 0.5rem 1rem;
                    text-decoration: none;
                    border-radius: 4px;
                    transition: background-color 0.3s ease;
                }

                .release-tag:hover {
                    background-color: #2980b9;
                }

                .plugin-list {
                    display: flex;
                    flex-direction: column;
                    gap: 1rem;
                }

                .plugin-card {
                    background-color: var(--card-background);
                    border-radius: 8px;
                    box-shadow: 0 4px 6px rgba(0,0,0,0.1);
                    padding: 1rem;
                    width: 100%;
                    display: flex;
                    flex-direction: column;
                    gap: 0.75rem;
                }

                .plugin-card.official {
                  background-color: #e3f3d6;
                }

                .plugin-header {
                    display: flex;
                    align-items: center;
                    gap: 1rem;
                }

                .plugin-icon {
                    width: 48px;
                    height: 48px;
                    border-radius: 4px;
                }

                .plugin-title {
                    font-size: 1.25rem;
                    font-weight: bold;
                    color: var(--primary-color);
                }

                .plugin-meta {
                    display: flex;
                    align-items: center;
                    gap: 1rem;
                    font-size: 0.9rem;
                    flex-wrap: wrap;
                }

                .plugin-meta-item {
                    display: flex;
                    align-items: center;
                    gap: 0.25rem;
                }

                .plugin-meta-label {
                    color: #6c757d;
                    font-weight: bold;
                    margin-right: 0.25rem;
                }

                .plugin-description {
                    color: #666;
                    margin-top: 0.5rem;
                }

                .plugin-links {
                    display: flex;
                    justify-content: space-between;
                    margin-top: 0.5rem;
                }
                
                .plugin-links a {
                  color: var(--primary-color);
                  align-items: center;
                }

                .plugin-download {
                    background-color: var(--secondary-color);
                    color: white;
                    text-decoration: none;
                    padding: 0.5rem 1rem;
                    border-radius: 4px;
                    transition: background-color 0.3s ease;
                }

                .plugin-download:hover {
                    background-color: #2980b9;
                }

                @media screen and (max-width: 600px) {
                    .container {
                        max-width: 100%;
                    }

                    .plugin-header {
                        flex-direction: column;
                        text-align: center;
                    }


                    .plugin-meta {
                        justify-content: center;
                    }

                    .plugin-links {
                        flex-direction: column;
                        gap: 0.5rem;
                        align-items: stretch;
                    }

                    .plugin-download, .plugin-repo {
                        text-align: center;
                        padding: 0.75rem;
                    }
                }
                """;
        }
    }
}