using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenDimmer;

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(Config))]
internal sealed partial class ConfigJson : JsonSerializerContext;

internal sealed class Config
{
    private const string FileName = "config.json";

    [JsonPropertyName("maxDim")]
    public double MaximumDim { get; set; } = DefaultMaxDim;

    private const double DefaultMaxDim = 0.90;

    private const double AbsoluteMaxDimCeiling = 0.95;

    [JsonPropertyName("perMonitor")]
    public bool PerMonitor { get; set; } = true;

    [JsonPropertyName("perVirtualDesktop")]
    public bool PerVirtualDesktop { get; set; }

    [JsonPropertyName("stepPercent")]
    public int StepPercent { get; set; } = 5;

    [JsonPropertyName("dimLevels")]
    public List<DimEntry> DimLevels { get; set; } = new();

    internal sealed class DimEntry
    {
        [JsonPropertyName("monitorId")]
        public string MonitorId { get; set; } = "*";

        [JsonPropertyName("desktopId")]
        public string DesktopId { get; set; } = "*";

        [JsonPropertyName("dim")]
        public double Dim { get; set; }
    }

    [JsonIgnore]
    internal static string Directory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    [JsonIgnore]
    internal static string FilePath => Path.Combine(Directory, FileName);

    internal static Config Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new Config();
            }

            var loaded = JsonSerializer.Deserialize(File.ReadAllText(FilePath), ConfigJson.Default.Config);
            if (loaded is null)
            {
                return new Config();
            }

            loaded.Normalize();
            return loaded;
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Config load failed, using defaults: {ex.Message}");
            return new Config();
        }
    }

    internal void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, ConfigJson.Default.Config));
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"Config save failed: {ex.Message}");
        }
    }

    private void Normalize()
    {
        if (StepPercent is < 1 or > 25)
        {
            StepPercent = 5;
        }

        MaximumDim = Math.Clamp(MaximumDim, 0.10, AbsoluteMaxDimCeiling);

        foreach (var entry in DimLevels)
        {
            entry.Dim = Math.Clamp(entry.Dim, 0.0, MaximumDim);
        }
    }
}
