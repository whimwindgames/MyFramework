using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MyFramework.BuildStudio;

namespace MyFramework.BuildStudio.Core;

public static class BuildStudioJson
{
    static readonly JsonSerializerOptions CompactOptions = makeOptions(false);
    static readonly JsonSerializerOptions PrettyOptions = makeOptions(true);

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? PrettyOptions : CompactOptions);

    public static T Deserialize<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, CompactOptions) ??
        throw new InvalidDataException($"{typeof(T).Name} JSON is empty.");

    public static string ComputeStructureHash(MfProjectStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        JsonObject root = JsonSerializer.SerializeToNode(structure, CompactOptions) as JsonObject ??
            throw new InvalidDataException("Project structure cannot be represented as JSON.");
        root["structureHash"] = string.Empty;
        string canonical = canonicalize(root).ToJsonString(CompactOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public static void WriteAtomic<T>(string path, T value)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Output path must be absolute.");
        string full = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(full) ??
            throw new InvalidDataException("Output directory is invalid.");
        Directory.CreateDirectory(parent);
        PathSecurity.EnsureNoLinks(parent);
        string temporary = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None))
            using (StreamWriter writer = new(stream, new UTF8Encoding(false, true)))
            {
                writer.Write(Serialize(value));
                writer.WriteLine();
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(temporary, full, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    static JsonNode canonicalize(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            JsonObject sorted = new();
            foreach ((string key, JsonNode? value) in obj.OrderBy(item => item.Key,
                         StringComparer.Ordinal))
                sorted[key] = canonicalize(value);
            return sorted;
        }
        if (node is JsonArray array)
        {
            JsonArray result = new();
            foreach (JsonNode? value in array) result.Add(canonicalize(value));
            return result;
        }
        return node?.DeepClone() ?? JsonValue.Create((string?)null)!;
    }

    static JsonSerializerOptions makeOptions(bool indented) => new()
    {
        IncludeFields = true,
        WriteIndented = indented,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
