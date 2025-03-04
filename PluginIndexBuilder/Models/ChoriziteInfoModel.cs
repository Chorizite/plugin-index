namespace Chorizite.PluginIndexBuilder.Models {
    public class ChoriziteInfoModel {
        public required ReleaseModel Latest { get; set; }
        public required ReleaseModel? LatestBeta { get; set; }
    }
}
