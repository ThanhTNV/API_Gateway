using System.Text.Json;
using System.Text.Json.Serialization;

namespace self_API_Gateway.Configuration;

// Thrown when a gateway configuration is rejected by validation.
public sealed class GatewayConfigException(string message) : Exception(message);

public sealed class ConfigChangeRecord
{
    public long Seq { get; set; }
    public DateTimeOffset Time { get; set; }
    public string Source { get; set; } = "";
    public string? RemoteIp { get; set; }
    public GatewayOptions Config { get; set; } = new();
}

public sealed class ConfigChangeResult
{
    public long Seq { get; set; }
    // True when the change touches settings that are only read at startup (KnownProxies, KnownNetworks).
    public bool RestartRequired { get; set; }
    public string Message { get; set; } = "";
}

public sealed class AdminError
{
    public string Error { get; set; } = "";
}

// Source-generated (AOT-safe) JSON for the admin API and the change log. Unknown properties are
// rejected so a typo in a submitted configuration fails instead of being silently dropped.
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(GatewayOptions))]
[JsonSerializable(typeof(ConfigChangeRecord))]
[JsonSerializable(typeof(ConfigChangeResult))]
[JsonSerializable(typeof(AdminError))]
internal partial class GatewayJsonContext : JsonSerializerContext
{
}

// Write-ahead log of configuration changes, one JSON record per line. A change is appended and
// flushed to disk before it is applied, and the last complete record is the configuration the
// gateway starts with, so the log is also what makes admin changes survive a restart.
public sealed class ConfigChangeLog(string filePath)
{
    public string FilePath { get; } = filePath;

    // A torn trailing line left by a crash mid-append is skipped in favour of the record before it.
    public ConfigChangeRecord? ReadLast(out int skippedLines)
    {
        skippedLines = 0;
        if (!File.Exists(FilePath)) return null;

        var lines = File.ReadAllLines(FilePath);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            try
            {
                if (JsonSerializer.Deserialize(lines[i], GatewayJsonContext.Default.ConfigChangeRecord) is { Config: not null } record)
                {
                    return record;
                }
            }
            catch (JsonException)
            {
            }
            skippedLines++;
        }
        return null;
    }

    public void Append(ConfigChangeRecord record)
    {
        if (Path.GetDirectoryName(FilePath) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length > 0)
        {
            // Never glue a record onto a torn line.
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n') stream.WriteByte((byte)'\n');
        }

        stream.Write(JsonSerializer.SerializeToUtf8Bytes(record, GatewayJsonContext.Default.ConfigChangeRecord));
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }
}
