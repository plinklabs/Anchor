using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FocusAgent.Core.Tests.Contracts;

/// <summary>
/// The files under contracts/ the agent is held to (#573): agent-hub.json,
/// agent-rest.json and witness.json. They write a shape as an object of
/// fields, a list as [element], or a type: string, number, boolean or
/// date-time, a type ending in ? may be null (or, on the witness link, left
/// out). These helpers build a value of a shape, to hand the agent as the other
/// side would send it, and check what the agent sent against one.
/// </summary>
internal static class ContractFiles
{
    /// <summary>A date-time as the other side writes it.</summary>
    public const string SampleDateTime = "2026-11-22T08:30:00+00:00";

    /// <summary>A file under contracts/, found above the test's output directory.</summary>
    public static JsonObject Load(string name)
    {
        var relative = Path.Combine("contracts", name);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path))
                return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        }
        throw new FileNotFoundException($"No {relative} above {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// A value of the shape with every field set, nullable ones too, so a field
    /// the agent doesn't read shows. A string is an id (a fresh GUID), which
    /// reads as a string and as an id alike. <paramref name="valueFor"/> names
    /// a field's value instead, by its name.
    /// </summary>
    public static JsonNode Sample(JsonNode shape, Func<string, JsonNode?>? valueFor = null) => shape switch
    {
        JsonObject fields => new JsonObject(fields.Select(f => KeyValuePair.Create(
            f.Key,
            (JsonNode?)(valueFor?.Invoke(f.Key) ?? Sample(f.Value!, valueFor))))),
        JsonArray element => new JsonArray(Sample(element[0]!, valueFor)),
        _ => shape.GetValue<string>().TrimEnd('?') switch
        {
            "string" => JsonValue.Create(Guid.NewGuid().ToString()),
            "date-time" => JsonValue.Create(SampleDateTime),
            "number" => JsonValue.Create(42),
            "boolean" => JsonValue.Create(true),
            var type => throw new NotSupportedException($"The contracts have no type {type}."),
        },
    };

    /// <summary>
    /// Where a value the agent sent differs from the shape: a field it leaves
    /// out that the shape requires, one the shape doesn't list, a value of
    /// another type, a null where the type doesn't allow one.
    /// <paramref name="mayLeaveOut"/>: a field whose type ends in ? may be
    /// absent too, as on the witness link.
    /// </summary>
    public static List<string> Mismatches(JsonNode shape, JsonNode? sent, string path, bool mayLeaveOut = false)
    {
        var problems = new List<string>();
        Check(shape, sent, path, mayLeaveOut, problems);
        return problems;
    }

    private static void Check(JsonNode shape, JsonNode? sent, string path, bool mayLeaveOut, List<string> problems)
    {
        switch (shape)
        {
            case JsonObject fields when sent is JsonObject value:
                foreach (var (name, field) in fields)
                {
                    if (value.TryGetPropertyValue(name, out var item))
                        Check(field!, item, $"{path}.{name}", mayLeaveOut, problems);
                    else if (!(mayLeaveOut && field is JsonValue leaf && leaf.GetValue<string>().EndsWith('?')))
                        problems.Add($"{path}.{name}: in the contract, but the agent left it out");
                }
                foreach (var (name, _) in value)
                {
                    if (!fields.ContainsKey(name))
                        problems.Add($"{path}.{name}: sent by the agent, but not in the contract");
                }
                break;
            case JsonArray element when sent is JsonArray items:
                foreach (var item in items)
                    Check(element[0]!, item, $"{path}[]", mayLeaveOut, problems);
                break;
            case JsonValue leaf:
                var type = leaf.GetValue<string>();
                if (sent is null)
                {
                    if (!type.EndsWith('?'))
                        problems.Add($"{path}: the agent sent null, which {type} doesn't allow");
                }
                else if (!IsA(type.TrimEnd('?'), sent))
                {
                    problems.Add($"{path}: the agent sent {sent.ToJsonString()}, which isn't a {type}");
                }
                break;
            default:
                problems.Add($"{path}: the agent sent {sent?.ToJsonString() ?? "null"} where the contract has {shape.ToJsonString()}");
                break;
        }
    }

    private static bool IsA(string type, JsonNode value) => type switch
    {
        "string" => value.GetValueKind() == JsonValueKind.String,
        "date-time" => value.GetValueKind() == JsonValueKind.String &&
                       DateTimeOffset.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
        "number" => value.GetValueKind() == JsonValueKind.Number,
        "boolean" => value.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        _ => false,
    };

    /// <summary>
    /// What the agent read out of a sample: the value it parsed into, written
    /// back the way the wire writes it (camelCase). Equal to the sample when
    /// the agent reads every field, and only those.
    /// </summary>
    public static JsonNode? AsRead<T>(T parsed) =>
        JsonSerializer.SerializeToNode(parsed, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>
    /// Where what the agent read differs from the sample it was handed: a field
    /// it didn't read, one it read that the sample doesn't have, a value it
    /// read differently.
    /// </summary>
    public static List<string> ReadDifferences(JsonNode? sample, JsonNode? read, string path)
    {
        var problems = new List<string>();
        Compare(sample, read, path, problems);
        return problems;
    }

    private static void Compare(JsonNode? sample, JsonNode? read, string path, List<string> problems)
    {
        switch (sample, read)
        {
            case (JsonObject handed, JsonObject parsed):
                foreach (var (name, value) in handed)
                {
                    if (parsed.TryGetPropertyValue(name, out var got))
                        Compare(value, got, $"{path}.{name}", problems);
                    else
                        problems.Add($"{path}.{name}: in the contract, but the agent doesn't read it");
                }
                foreach (var (name, _) in parsed)
                {
                    if (!handed.ContainsKey(name))
                        problems.Add($"{path}.{name}: the agent reads it, but it isn't in the contract");
                }
                break;
            case (JsonArray handed, JsonArray parsed) when handed.Count == parsed.Count:
                for (var i = 0; i < handed.Count; i++)
                    Compare(handed[i], parsed[i], $"{path}[{i}]", problems);
                break;
            case (JsonValue handed, JsonValue parsed) when Same(handed, parsed):
                break;
            default:
                problems.Add($"{path}: handed {sample?.ToJsonString() ?? "null"}, the agent read {read?.ToJsonString() ?? "null"}");
                break;
        }
    }

    // A date-time is the same instant however it is written.
    private static bool Same(JsonValue handed, JsonValue parsed) =>
        JsonNode.DeepEquals(handed, parsed) ||
        (handed.GetValueKind() == JsonValueKind.String && parsed.GetValueKind() == JsonValueKind.String &&
         DateTimeOffset.TryParse(handed.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var a) &&
         DateTimeOffset.TryParse(parsed.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var b) &&
         a == b && !Guid.TryParse(handed.GetValue<string>(), out _));
}
