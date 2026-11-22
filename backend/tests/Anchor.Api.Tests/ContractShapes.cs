using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Anchor.Api.Tests;

/// <summary>
/// Describes types the way the files under contracts/ write shapes (rest.json,
/// agent-rest.json, agent-hub.json), through the JSON settings they go over the
/// wire with: an object's fields by their wire names, a list by its element, a
/// leaf by its type (? when its annotation allows null), an enum by its name,
/// with the values it goes over the wire as kept in <see cref="Enums"/>.
/// </summary>
internal sealed class ContractShapeDescriber(JsonSerializerOptions options)
{
    private readonly NullabilityInfoContext _nullability = new();
    private readonly Dictionary<string, Type> _enumTypes = new(StringComparer.Ordinal);

    public Dictionary<string, JsonArray> Enums { get; } = new(StringComparer.Ordinal);

    /// <summary>Every field of each response a scenario got, not only those a file lists.</summary>
    public JsonObject Responses { get; } = new();

    public JsonNode Describe(Type type) => Describe(type, nullable: false);

    public JsonNode Describe(Type type, bool nullable)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return Describe(underlying, nullable: true);
        if (type.IsEnum)
            return Leaf(EnumName(type), nullable);
        if (type == typeof(string) || type == typeof(Guid))
            return Leaf("string", nullable);
        if (type == typeof(DateTimeOffset) || type == typeof(DateTime))
            return Leaf("date-time", nullable);
        if (type == typeof(bool))
            return Leaf("boolean", nullable);
        if (type.IsPrimitive || type == typeof(decimal))
            return Leaf("number", nullable);

        var info = options.GetTypeInfo(type);
        return info.Kind switch
        {
            JsonTypeInfoKind.Enumerable => new JsonArray(Describe(info.ElementType!, nullable: false)),
            JsonTypeInfoKind.Object => new JsonObject(info.Properties.Select(p =>
                KeyValuePair.Create(p.Name, (JsonNode?)Describe(p.PropertyType, IsNullable(p))))),
            _ => throw new NotSupportedException(
                $"{type} goes over the wire as {info.Kind}, which the contracts have no shape for."),
        };
    }

    /// <summary>Whether a parameter's or return value's annotation allows null.</summary>
    public bool IsNullable(ParameterInfo parameter)
    {
        var info = _nullability.Create(parameter);
        // An awaited answer: the annotation on the task's result.
        if (parameter.ParameterType.IsGenericType &&
            parameter.ParameterType.GetGenericTypeDefinition() == typeof(Task<>))
            info = info.GenericTypeArguments[0];
        return info.ReadState == NullabilityState.Nullable;
    }

    private static JsonValue Leaf(string type, bool nullable) => JsonValue.Create(nullable ? type + "?" : type);

    private bool IsNullable(JsonPropertyInfo property) => property.AttributeProvider switch
    {
        PropertyInfo p => _nullability.Create(p).ReadState == NullabilityState.Nullable,
        FieldInfo f => _nullability.Create(f).ReadState == NullabilityState.Nullable,
        _ => !property.PropertyType.IsValueType,
    };

    private string EnumName(Type type)
    {
        if (_enumTypes.TryGetValue(type.Name, out var known) && known != type)
            throw new NotSupportedException($"Two enums named {type.Name}: {known} and {type}.");
        _enumTypes[type.Name] = type;
        Enums[type.Name] = new JsonArray([.. Enum.GetValues(type).Cast<object>()
            .Select(v => JsonSerializer.SerializeToNode(v, type, options))]);
        return type.Name;
    }

    /// <summary>
    /// Checks a description against a body that went over the wire: every
    /// field there, null only where the type allows it, every value of its
    /// type.
    /// </summary>
    public void Check(JsonNode shape, JsonNode? body, string path, List<string> problems)
    {
        switch (shape)
        {
            case JsonObject fields when body is JsonObject sent:
                foreach (var (name, field) in fields)
                {
                    if (sent.TryGetPropertyValue(name, out var value))
                        Check(field!, value, $"{path}.{name}", problems);
                    else
                        problems.Add($"  {path}.{name}: described, but not in the body the backend sent");
                }
                break;
            case JsonArray element when body is JsonArray sent:
                foreach (var item in sent)
                    Check(element[0]!, item, $"{path}[]", problems);
                break;
            case JsonValue leaf when leaf.GetValueKind() == JsonValueKind.String:
                var type = leaf.GetValue<string>();
                if (body is null)
                {
                    if (!type.EndsWith('?'))
                        problems.Add($"  {path}: the backend sent null, which {type} doesn't allow");
                    break;
                }
                if (!IsA(type.TrimEnd('?'), body))
                    problems.Add($"  {path}: the backend sent {ContractJson.Text(body)}, which isn't a {type}");
                break;
            default:
                problems.Add($"  {path}: the backend sent {ContractJson.Text(body)} where it describes {ContractJson.Text(shape)}");
                break;
        }
    }

    private bool IsA(string type, JsonNode value) => type switch
    {
        "string" => value.GetValueKind() == JsonValueKind.String,
        "date-time" => value.GetValueKind() == JsonValueKind.String &&
                       DateTimeOffset.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
        "number" => value.GetValueKind() == JsonValueKind.Number,
        "boolean" => value.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        _ => Enums.TryGetValue(type, out var values) && values.Any(v => JsonNode.DeepEquals(v, value)),
    };
}

/// <summary>Reading the files under contracts/ and comparing what they say with what the backend serves.</summary>
internal static class ContractJson
{
    /// <summary>A file under contracts/, found above the test's output directory.</summary>
    public static JsonObject Load(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(path))
                return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        }
        throw new FileNotFoundException($"No {relativePath} above {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// What the backend serves, cut down to the fields the file lists: extra
    /// fields are safe, the client doesn't read them. A field the file lists
    /// that the backend doesn't serve stays, as "(not served)".
    /// </summary>
    public static JsonNode? Project(JsonNode? served, JsonNode? inFile) => (served, inFile) switch
    {
        (JsonObject fields, JsonObject listed) => new JsonObject(listed.Select(p => KeyValuePair.Create(
            p.Key,
            fields.TryGetPropertyValue(p.Key, out var field) ? Project(field, p.Value) : JsonValue.Create("(not served)")))),
        (JsonArray { Count: 1 } element, JsonArray { Count: 1 } listed) => new JsonArray(Project(element[0], listed[0])),
        _ => served?.DeepClone(),
    };

    /// <summary>The type names in a shape, enum names among them.</summary>
    public static IEnumerable<string> Types(JsonNode? shape) => shape switch
    {
        JsonObject fields => fields.SelectMany(f => Types(f.Value)),
        JsonArray elements => elements.SelectMany(Types),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => [value.GetValue<string>()],
        _ => [],
    };

    public static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Distinct().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    public static IEnumerable<string> Differences(JsonObject inFile, JsonObject served) =>
        inFile.Select(p => p.Key).Union(served.Select(p => p.Key))
            .Select(name => (
                Name: name,
                InFile: Canonical(inFile.TryGetPropertyValue(name, out var f) ? f : null),
                Served: Canonical(served.TryGetPropertyValue(name, out var s) ? s : null)))
            .Where(d => d.InFile != d.Served)
            .Select(d => $"  {d.Name}: the file says {d.InFile}, the backend serves {d.Served}");

    /// <summary>
    /// A form that ignores order: fields, query parameters, audiences and enum
    /// values are sets, and the files list them in whatever order reads best.
    /// </summary>
    public static string Canonical(JsonNode? node) => Sorted(node)?.ToJsonString() ?? "null";

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray a => new JsonArray(a
            .Select(Sorted)
            .OrderBy(n => n?.ToJsonString(), StringComparer.Ordinal)
            .ToArray()),
        _ => node?.DeepClone(),
    };

    public static string Indented(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public static string Text(JsonNode? node) =>
        node?.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
}
