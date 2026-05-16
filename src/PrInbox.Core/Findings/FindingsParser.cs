using System.Reflection;
using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PrInbox.Core.Findings;

/// <summary>
/// Load + validate <c>findings.yaml</c> files. Supports two modes:
/// <list type="bullet">
///   <item><see cref="ParseStrict"/> — validates against the embedded JSON schema
///         and throws on any error.</item>
///   <item><see cref="ParseLenient"/> — surfaces all issues as a
///         <see cref="ParseResult"/> without throwing, so a UI can render
///         partial-content with warnings.</item>
/// </list>
/// </summary>
public sealed class FindingsParser
{
    private static readonly Lazy<JsonSchema> Schema = new(LoadEmbeddedSchema);

    private readonly IDeserializer _yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly ISerializer _yamlOut = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .DisableAliases()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>The raw JSON Schema text (embedded as a resource).</summary>
    public static string SchemaJson => LoadEmbeddedSchemaJson();

    /// <summary>Parse + validate; throws on any schema violation.</summary>
    public FindingsDocument ParseStrict(string yamlText)
    {
        var result = ParseLenient(yamlText);
        if (result.Errors.Count > 0)
        {
            throw new FormatException("findings.yaml is not schema-valid:\n  - " +
                string.Join("\n  - ", result.Errors));
        }
        return result.Document
            ?? throw new FormatException("findings.yaml parsed to null document.");
    }

    /// <summary>Parse + validate without throwing; returns partial document plus error list.</summary>
    public ParseResult ParseLenient(string yamlText)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            return new ParseResult(null, new[] { "findings.yaml is empty" });
        }

        object? raw;
        try
        {
            raw = _yaml.Deserialize<object>(yamlText);
        }
        catch (Exception ex)
        {
            return new ParseResult(null, new[] { $"YAML parse error: {ex.Message}" });
        }

        var json = YamlToJson(raw) ?? new JsonObject();
        var validation = Schema.Value.Evaluate(json, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            EvaluateAs = SpecVersion.Draft202012,
        });

        var errors = new List<string>();
        if (!validation.IsValid)
        {
            foreach (var detail in FlattenErrors(validation))
            {
                errors.Add(detail);
            }
        }

        FindingsDocument? doc = null;
        try
        {
            doc = DeserializeDocument(yamlText);
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException is null ? "" : $" / {ex.InnerException.Message}";
            errors.Add($"Typed parse error: {ex.Message}{inner}");
        }

        return new ParseResult(doc, errors);
    }

    /// <summary>Serialize a <see cref="FindingsDocument"/> back to YAML text.</summary>
    public string Serialize(FindingsDocument doc)
    {
        var projection = new
        {
            schema_version = doc.SchemaVersion,
            pr_url = doc.PrUrl,
            pr_stable_identity = doc.PrStableIdentity,
            head_sha = doc.HeadSha,
            base_sha = doc.BaseSha,
            generated_at_utc = doc.GeneratedAtUtc.UtcDateTime.ToString("O"),
            models = doc.Models,
            asymmetry = doc.Asymmetry,
            findings = doc.Findings.Select(f => new
            {
                id = f.Id,
                severity = f.Severity.ToYamlValue(),
                confidence = f.Confidence.ToYamlValue(),
                found_by = f.FoundBy,
                file = f.File,
                line = f.Line,
                line_end = f.LineEnd,
                diff_anchorable = f.DiffAnchorable,
                title = f.Title,
                body = f.Body,
                suggested_inline = f.SuggestedInline,
            }).ToList(),
        };
        return _yamlOut.Serialize(projection);
    }

    private FindingsDocument DeserializeDocument(string yamlText)
    {
        var raw = _yaml.Deserialize<RawDocument>(yamlText)
            ?? throw new FormatException("findings.yaml deserialized to null");

        return new FindingsDocument
        {
            SchemaVersion = raw.SchemaVersion,
            PrUrl = raw.PrUrl ?? string.Empty,
            PrStableIdentity = raw.PrStableIdentity,
            HeadSha = raw.HeadSha ?? string.Empty,
            BaseSha = raw.BaseSha,
            GeneratedAtUtc = raw.GeneratedAtUtc,
            Models = (IReadOnlyList<string>?)raw.Models ?? Array.Empty<string>(),
            Asymmetry = raw.Asymmetry,
            Findings = ((IEnumerable<RawFinding>?)raw.Findings ?? Array.Empty<RawFinding>()).Select(MapFinding).ToList(),
        };
    }

    private static Finding MapFinding(RawFinding raw) => new()
    {
        Id = raw.Id ?? string.Empty,
        Severity = FindingEnumExtensions.ParseSeverity(raw.Severity ?? "medium"),
        Confidence = FindingEnumExtensions.ParseConfidence(raw.Confidence ?? "medium"),
        FoundBy = (IReadOnlyList<string>?)raw.FoundBy ?? Array.Empty<string>(),
        File = raw.File ?? string.Empty,
        Line = raw.Line,
        LineEnd = raw.LineEnd,
        DiffAnchorable = raw.DiffAnchorable,
        Title = raw.Title ?? string.Empty,
        Body = raw.Body,
        SuggestedInline = raw.SuggestedInline,
    };

    private static JsonNode? YamlToJson(object? value)
    {
        return value switch
        {
            null => null,
            string s => ScalarStringToJson(s),
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            DateTime dt => JsonValue.Create(dt.ToString("O")),
            DateTimeOffset dto => JsonValue.Create(dto.ToString("O")),
            IDictionary<object, object> map => DictToJson(map),
            IEnumerable<object> list => ListToJson(list),
            _ => JsonValue.Create(value.ToString() ?? string.Empty),
        };
    }

    /// <summary>
    /// YamlDotNet's untyped deserialize returns every scalar as a string.
    /// Apply YAML 1.2 core schema implicit typing so schema validation
    /// sees integers as integers and booleans as booleans.
    /// </summary>
    private static JsonNode? ScalarStringToJson(string s)
    {
        if (s == "null" || s == "~" || s == string.Empty) return null;
        if (s == "true" || s == "True" || s == "TRUE") return JsonValue.Create(true);
        if (s == "false" || s == "False" || s == "FALSE") return JsonValue.Create(false);

        if (long.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var l))
        {
            if (l >= int.MinValue && l <= int.MaxValue) return JsonValue.Create((int)l);
            return JsonValue.Create(l);
        }

        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            return JsonValue.Create(d);
        }

        return JsonValue.Create(s);
    }

    private static JsonObject DictToJson(IDictionary<object, object> map)
    {
        var obj = new JsonObject();
        foreach (var kv in map)
        {
            obj[kv.Key.ToString() ?? string.Empty] = YamlToJson(kv.Value);
        }
        return obj;
    }

    private static JsonArray ListToJson(IEnumerable<object> list)
    {
        var arr = new JsonArray();
        foreach (var item in list)
        {
            arr.Add(YamlToJson(item));
        }
        return arr;
    }

    private static IEnumerable<string> FlattenErrors(EvaluationResults results)
    {
        if (results.Errors is not null)
        {
            foreach (var kv in results.Errors)
            {
                yield return $"{results.InstanceLocation}: {kv.Value}";
            }
        }
        if (results.Details is not null)
        {
            foreach (var detail in results.Details)
            {
                foreach (var s in FlattenErrors(detail)) yield return s;
            }
        }
    }

    private static JsonSchema LoadEmbeddedSchema()
    {
        var json = LoadEmbeddedSchemaJson();
        return JsonSchema.FromText(json);
    }

    private static string LoadEmbeddedSchemaJson()
    {
        var asm = typeof(FindingsParser).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("findings.schema.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("findings.schema.json embedded resource not found.");
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Resource '{name}' not loadable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class RawDocument
    {
        public int SchemaVersion { get; set; }
        public string? PrUrl { get; set; }
        public string? PrStableIdentity { get; set; }
        public string? HeadSha { get; set; }
        public string? BaseSha { get; set; }
        public DateTimeOffset GeneratedAtUtc { get; set; }
        public List<string>? Models { get; set; }
        public AsymmetryStats? Asymmetry { get; set; }
        public List<RawFinding>? Findings { get; set; }
    }

    private sealed class RawFinding
    {
        public string? Id { get; set; }
        public string? Severity { get; set; }
        public string? Confidence { get; set; }
        public List<string>? FoundBy { get; set; }
        public string? File { get; set; }
        public int? Line { get; set; }
        public int? LineEnd { get; set; }
        public bool DiffAnchorable { get; set; }
        public string? Title { get; set; }
        public string? Body { get; set; }
        public string? SuggestedInline { get; set; }
    }
}

/// <summary>Lenient-parse result. <see cref="Document"/> may be partial; <see cref="Errors"/> holds schema/parse complaints.</summary>
public sealed record ParseResult(FindingsDocument? Document, IReadOnlyList<string> Errors);
