using System.Text.Json.Serialization;

namespace CoilViewer;

[JsonSerializable(typeof(ViewerConfig))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ViewerConfigJsonContext : JsonSerializerContext;
