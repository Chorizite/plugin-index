using System;

namespace Chorizite.PluginIndexBuilder.Models {
    public class ReleaseModel {
        public required string Version { get; set; }
        public required string DownloadUrl { get; set; }
        public required string Sha256 { get; set; }
        public required int Downloads { get; set; }
        public required DateTime Created { get; set; }
        public required DateTime Updated { get; set; }
        public required bool HasReleaseModifications { get; set; }
    }
}