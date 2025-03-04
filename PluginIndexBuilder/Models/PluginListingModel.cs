namespace Chorizite.PluginIndexBuilder.Models {
    public class PluginListingModel {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public required string Website { get; set; }
        public required string Description { get; set; }
        public required string Author { get; set; }
        public required bool IsDefault { get; set; }
        public required bool IsOfficial { get; set; }
        public required int TotalDownloads { get; set; }
        public required ReleaseModel Latest { get; set; }
        public required ReleaseModel? LatestBeta { get; set; }
    }
}