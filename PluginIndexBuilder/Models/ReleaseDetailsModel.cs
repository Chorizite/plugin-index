using Chorizite.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Chorizite.PluginIndexBuilder.Models {
    public class ReleaseDetailsModel {
        public required string Name { get; set; }
        public required string Changelog { get; set; }
        public required string DownloadUrl { get; set; }
        public required string Sha256 { get; set; }
        public required string Version { get; set; }
        public required bool IsBeta { get; set; }
        public required int Downloads { get; set; }
        public required DateTime Created { get; set; }
        public required DateTime Updated { get; set; }
        public required List<string> Dependencies { get; set; }
        public required List<string> Environments { get; set; }
    }
}