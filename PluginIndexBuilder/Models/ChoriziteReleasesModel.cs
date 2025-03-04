using NJsonSchema.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Chorizite.PluginIndexBuilder.Models {

    [JsonSchemaExtensionData("description", "Chorizite release details.")]
    public class ChoriziteReleasesModel {
        [JsonPropertyName("$schema")]
        public string Schema => "https://chorizite.github.io/plugin-index/schemas/chorizite-releases-schema.json";

        [JsonSchemaExtensionData("description", "The total number of downloads for chorizite.")]
        public required int TotalDownloads { get; set; }

        [JsonSchemaExtensionData("description", "The list of available releases for chorizite.")]
        public required List<ReleaseDetailsModel> Releases { get; set; }
    }
}
