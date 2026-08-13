using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var options = Arguments.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);
var entries = new List<JsonObject>();
foreach (var path in Directory.EnumerateFiles(options.SourceDirectory, "*.json").Order(StringComparer.Ordinal))
{
    var node = JsonNode.Parse(await File.ReadAllTextAsync(path))?.AsObject()
        ?? throw new InvalidDataException($"Catalog entry is empty: {path}");
    ValidateEntry(node, path);
    var pluginId = RequiredString(node, "pluginId");
    var packagePath = options.Packages.TryGetValue(pluginId, out var local)
        ? local
        : await DownloadAsync(RequiredString(node["artifact"]!.AsObject(), "url"), options.DownloadDirectory);
    await ValidatePackageAsync(node, packagePath);
    entries.Add(node);
}
if (entries.Select(value => RequiredString(value, "pluginId")).Distinct(StringComparer.Ordinal).Count() != entries.Count)
    throw new InvalidDataException("Catalog contains duplicate PluginIds.");
var production = new JsonObject
{
    ["schemaVersion"] = 1,
    ["entries"] = new JsonArray(entries.OrderBy(value => RequiredString(value, "pluginId"), StringComparer.Ordinal).Select(value => value.DeepClone()).ToArray()),
    ["presets"] = LoadPresets(Path.Combine(Path.GetDirectoryName(options.SourceDirectory)!, "presets"))
};
await File.WriteAllTextAsync(options.OutputPath, production.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
Console.WriteLine($"Validated {entries.Count} package(s); wrote {options.OutputPath}");

static void ValidateEntry(JsonObject entry, string path)
{
    if (entry["schemaVersion"]?.GetValue<int>() != 1) throw new InvalidDataException($"Unsupported schemaVersion: {path}");
    var id = RequiredString(entry, "pluginId");
    if (!Regex.IsMatch(id, "^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+$"))
        throw new InvalidDataException($"Invalid PluginId: {id}");
    var version = RequiredString(entry, "version");
    if (!Regex.IsMatch(version, "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$"))
        throw new InvalidDataException($"Invalid SemVer: {version}");
    if (entry["channel"]?.GetValue<string>() is not ("stable" or "prerelease" or "candidate"))
        throw new InvalidDataException("Invalid channel.");
    if (entry["provenance"]?.GetValue<string>() != "officialCatalog") throw new InvalidDataException("Invalid provenance.");
    if (entry["trustClassification"]?.GetValue<string>() is not ("official" or "officialCurated")) throw new InvalidDataException("Invalid trust classification.");
    var artifact = entry["artifact"]?.AsObject() ?? throw new InvalidDataException("artifact is required.");
    if (!Uri.TryCreate(RequiredString(artifact, "url"), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        throw new InvalidDataException("Artifact URL must use HTTPS.");
    var sha = RequiredString(artifact, "sha256");
    if (!Regex.IsMatch(sha, "^[0-9a-f]{64}$")) throw new InvalidDataException("Artifact SHA-256 must be lowercase hexadecimal.");
    foreach (var requiredArray in new[] { "architectures", "requestedHostServices", "artifactFormats", "artifactTransformers", "restoreStrategies" })
        if (entry[requiredArray] is not JsonArray) throw new InvalidDataException($"{requiredArray} must be an array.");
}

static JsonArray LoadPresets(string directory)
{
    var result = new JsonArray();
    if (!Directory.Exists(directory)) return result;
    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
    {
        var value = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException($"Preset is empty: {path}");
        if (value["schemaVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("Unsupported Preset schema.");
        var id = RequiredString(value, "presetId");
        if (!ids.Add(id)) throw new InvalidDataException("Duplicate Preset identity.");
        var actions = value["actions"]?.AsArray() ?? throw new InvalidDataException("Preset actions are required.");
        if (actions.Count is 0 or > 32) throw new InvalidDataException("Preset action count is outside its bound.");
        foreach (var action in actions.Select(node => node!.AsObject()))
        {
            var type = RequiredString(action, "type");
            if (type is not ("installPlugin" or "enablePlugin" or "setHostFeature" or "setupExternalIntegration" or "notice"))
                throw new InvalidDataException("Preset contains a forbidden action type.");
            if (type == "setupExternalIntegration")
            {
                var url = RequiredString(action, "url");
                var sha = RequiredString(action, "sha256");
                if (!url.StartsWith("https://", StringComparison.Ordinal) || !Regex.IsMatch(sha, "^[0-9a-f]{64}$"))
                    throw new InvalidDataException("External integration requires a fixed HTTPS URL and SHA-256.");
            }
        }
        result.Add(value.DeepClone());
    }
    return result;
}

static async Task ValidatePackageAsync(JsonObject catalog, string packagePath)
{
    await using var input = File.OpenRead(packagePath);
    var sha = Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant();
    var artifact = catalog["artifact"]!.AsObject();
    if (!StringComparer.Ordinal.Equals(sha, RequiredString(artifact, "sha256")))
        throw new InvalidDataException("Catalog SHA-256 does not match package.");
    using var archive = ZipFile.OpenRead(packagePath);
    if (archive.Entries.Any(value => value.FullName.EndsWith("FolderRewind.Plugin.Abstractions.dll", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidDataException("Package bundles Abstractions DLL.");
    var manifestEntry = archive.Entries.SingleOrDefault(value => value.FullName == "manifest.json")
        ?? throw new InvalidDataException("Package has no root manifest.json.");
    using var stream = manifestEntry.Open();
    using var manifest = await JsonDocument.ParseAsync(stream);
    var root = manifest.RootElement;
    Equal(RequiredString(catalog, "pluginId"), root.GetProperty("pluginId").GetString(), "PluginId");
    Equal(RequiredString(catalog, "version"), root.GetProperty("version").GetString(), "Version");
    var api = catalog["pluginApi"]!.AsObject();
    if (api["major"]!.GetValue<int>() != root.GetProperty("pluginApi").GetProperty("major").GetInt32()
        || api["minor"]!.GetValue<int>() != root.GetProperty("pluginApi").GetProperty("minor").GetInt32())
        throw new InvalidDataException("Catalog API does not match manifest.");
    Sequence(catalog["architectures"]!.AsArray(), root.GetProperty("architectures"), "architectures");
    Sequence(catalog["requestedHostServices"]!.AsArray(), root.GetProperty("requestedHostServices"), "requestedHostServices");
    SummaryCount(catalog, root, "artifactFormats");
    SummaryCount(catalog, root, "artifactTransformers");
    SummaryCount(catalog, root, "restoreStrategies");
}

static void SummaryCount(JsonObject catalog, JsonElement manifest, string name)
{
    if (catalog[name]!.AsArray().Count != manifest.GetProperty(name).GetArrayLength())
        throw new InvalidDataException($"Catalog {name} summary does not match manifest.");
}

static void Sequence(JsonArray expected, JsonElement actual, string name)
{
    var left = expected.Select(value => value!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
    var right = actual.EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.Ordinal).ToArray();
    if (!left.SequenceEqual(right, StringComparer.Ordinal)) throw new InvalidDataException($"Catalog {name} does not match manifest.");
}

static void Equal(string expected, string? actual, string name)
{
    if (!StringComparer.Ordinal.Equals(expected, actual)) throw new InvalidDataException($"Catalog {name} does not match manifest.");
}

static string RequiredString(JsonObject value, string property)
    => value[property]?.GetValue<string>() is { Length: > 0 } text ? text : throw new InvalidDataException($"{property} is required.");

static async Task<string> DownloadAsync(string url, string downloadDirectory)
{
    Directory.CreateDirectory(downloadDirectory);
    var path = Path.Combine(downloadDirectory, Guid.NewGuid().ToString("N") + ".frplugin");
    using var client = new HttpClient();
    await using var output = File.Create(path);
    await using var input = await client.GetStreamAsync(url);
    await input.CopyToAsync(output);
    return path;
}

sealed record Arguments(string SourceDirectory, string OutputPath, Dictionary<string, string> Packages)
{
    public string OutputDirectory => Path.GetDirectoryName(OutputPath)!;
    public string DownloadDirectory => Path.Combine(OutputDirectory, ".downloads");

    public static Arguments Parse(string[] args)
    {
        string source = "source", output = "public/catalog.v1.json";
        var packages = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--source": source = args[++index]; break;
                case "--output": output = args[++index]; break;
                case "--package":
                    var pair = args[++index].Split('=', 2);
                    if (pair.Length != 2) throw new ArgumentException("--package expects PluginId=path.");
                    packages.Add(pair[0], Path.GetFullPath(pair[1]));
                    break;
                default: throw new ArgumentException($"Unknown argument: {args[index]}");
            }
        }
        return new Arguments(Path.GetFullPath(source), Path.GetFullPath(output), packages);
    }
}
