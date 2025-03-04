using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Chorizite.PluginIndexBuilder.Models {
    public class PluginDetailsModel {
        [JsonPropertyName("$schema")]
        public string Schema => "https://chorizite.github.io/plugin-index/schemas/plugin-details-schema.json";
        public required string Id { get; set; }
        public required string Name { get; set; }
        public required string Website { get; set; }
        public required string Author { get; set; }
        public required string Description { get; set; }
        public required bool IsDefault { get; set; }
        public required bool IsOfficial { get; set; }
        public required int TotalDownloads { get; set; }
        public required List<ReleaseDetailsModel> Releases { get; set; }
    }
}
