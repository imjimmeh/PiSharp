using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Eval.Kernels;

namespace PiSharp.Eval.Bench;

/// <summary>Raised when a bench spec is malformed or fails validation.</summary>
public sealed class BenchSpecException(string message) : Exception(message);

/// <summary>
/// Parses bench spec JSON (snake_case-friendly, <c>AgentJsonSerializer</c>-compatible
/// conventions) into <see cref="BenchSpec"/> and validates: name required, runs ≥ 1, at
/// least one case, unique case names, at most one expected matcher, checker kernel known.
/// </summary>
public static class BenchSpecParser
{
    public static readonly JsonSerializerOptions SpecJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static BenchSpec Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new BenchSpecException("Bench spec is empty.");

        BenchSpec? spec;
        try
        {
            spec = JsonSerializer.Deserialize<BenchSpec>(json, SpecJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new BenchSpecException($"Bench spec is not valid JSON: {ex.Message}");
        }

        if (spec is null) throw new BenchSpecException("Bench spec is empty.");
        Validate(spec);
        return spec;
    }

    public static BenchSpec Parse(JsonElement element)
    {
        BenchSpec? spec;
        try
        {
            spec = element.Deserialize<BenchSpec>(SpecJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new BenchSpecException($"Bench spec is not valid JSON: {ex.Message}");
        }

        if (spec is null) throw new BenchSpecException("Bench spec is empty.");
        Validate(spec);
        return spec;
    }

    public static void Validate(BenchSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Name))
            throw new BenchSpecException("Bench spec 'name' is required.");

        if (spec.Runs < 1)
            throw new BenchSpecException($"Bench spec '{spec.Name}' runs must be >= 1 (got {spec.Runs}).");

        if (spec.Cases is null || spec.Cases.Count == 0)
            throw new BenchSpecException($"Bench spec '{spec.Name}' must declare at least one case.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in spec.Cases)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
                throw new BenchSpecException($"Bench spec '{spec.Name}' has a case with no name.");
            if (!seen.Add(c.Name))
                throw new BenchSpecException($"Bench spec '{spec.Name}' has duplicate case name '{c.Name}'.");

            if (c.Expected is not null)
            {
                var matchers = 0;
                if (c.Expected.Text is not null) matchers++;
                if (c.Expected.Regex is not null) matchers++;
                if (c.Expected.Fixture is not null) matchers++;
                if (matchers > 1)
                    throw new BenchSpecException($"Case '{c.Name}' may specify at most one of expected.text/regex/fixture.");
            }

            if (c.Checker is not null && !string.IsNullOrWhiteSpace(c.Checker.Kernel))
            {
                if (EvalKernelRegistry.FindFactory(c.Checker.Kernel) is null)
                    throw new BenchSpecException(
                        $"Case '{c.Name}' checker uses unknown kernel '{c.Checker.Kernel}'. Registered kernels: {string.Join(", ", EvalKernelRegistry.Factories.Select(f => f.KernelName))}.");
            }
        }
    }
}
