using System.Text.Json;

namespace Scramble.Marmot.Tests.Convergence;

/// <summary>A step in a conformance scenario.</summary>
/// <param name="Type">The step's discriminator, e.g. <c>create_group</c>.</param>
/// <param name="Raw">The step's own JSON, for reading its fields.</param>
public sealed record ScenarioStep(string Type, JsonElement Raw)
{
    /// <summary>A required string field.</summary>
    public string String(string name) => Raw.GetProperty(name).GetString()!;

    /// <summary>An optional string field.</summary>
    public string? StringOrNull(string name) =>
        Raw.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A string array, empty when absent.</summary>
    public IReadOnlyList<string> Strings(string name)
    {
        if (!Raw.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray().Select(item => item.GetString()!).ToList();
    }
}

/// <summary>An assertion the scenario makes about the final state.</summary>
/// <param name="Type">The outcome's discriminator.</param>
/// <param name="Raw">The outcome's own JSON.</param>
public sealed record ExpectedOutcome(string Type, JsonElement Raw)
{
    /// <summary>A required string field.</summary>
    public string String(string name) => Raw.GetProperty(name).GetString()!;

    /// <summary>An optional unsigned field.</summary>
    public ulong? UInt64OrNull(string name) =>
        Raw.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetUInt64()
            : null;

    /// <summary>An optional boolean field.</summary>
    public bool? BoolOrNull(string name) =>
        Raw.TryGetProperty(name, out JsonElement value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>An optional string field.</summary>
    public string? StringOrNull(string name) =>
        Raw.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A string array, empty when absent.</summary>
    public IReadOnlyList<string> Strings(string name)
    {
        if (!Raw.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray().Select(item => item.GetString()!).ToList();
    }
}

/// <summary>
/// One of upstream's conformance scenarios, as written.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the only convergence tests in the repo that we did not write.</b>
/// Everything else can confirm at most that the implementation does what its
/// author expected — which is worth little for branch selection, where the
/// failure mode is a rule reproduced backwards from a correct reading of
/// upstream's source. A fork resolves by every member computing the same
/// answer, so agreeing with ourselves proves nothing.
/// </para>
/// <para>
/// They are a different shape from the byte fixtures under
/// <c>vectors/marmot/</c>: not an encoding to reproduce, but a <i>script</i> —
/// clients, a step list, and assertions about where everyone ends up. Running
/// one means simulating the whole group.
/// </para>
/// <para>
/// Copied verbatim from <c>mdk@wn-agent-v0.9.17</c>
/// <c>crates/cgka-conformance-simulator/vectors/</c>, and verified byte-identical
/// when copied. <b>A vector that starts failing after a pin bump is the signal
/// it exists to give — refresh it from the new tag deliberately, never edit one
/// to make it pass.</b>
/// </para>
/// </remarks>
/// <param name="Name">The scenario's name.</param>
/// <param name="ConformanceVersion">The upstream version that produced it.</param>
/// <param name="Clients">Every client the scenario names.</param>
/// <param name="Steps">The script, in order.</param>
/// <param name="ExpectedOutcomes">What must hold at the end.</param>
public sealed record ScenarioVector(
    string Name,
    string ConformanceVersion,
    IReadOnlyList<string> Clients,
    IReadOnlyList<ScenarioStep> Steps,
    IReadOnlyList<ExpectedOutcome> ExpectedOutcomes)
{
    /// <summary>Loads a vector by file name.</summary>
    public static ScenarioVector Load(string fileName)
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "vectors", "convergence", fileName);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement.Clone();
        JsonElement scenario = root.GetProperty("scenario");

        return new ScenarioVector(
            root.GetProperty("scenario_name").GetString()!,
            root.GetProperty("conformance_version").GetString()!,
            scenario.GetProperty("clients").EnumerateArray()
                .Select(c => c.GetString()!).ToList(),
            scenario.GetProperty("steps").EnumerateArray()
                .Select(s => new ScenarioStep(s.GetProperty("type").GetString()!, s)).ToList(),
            root.GetProperty("expected_outcomes").EnumerateArray()
                .Select(o => new ExpectedOutcome(o.GetProperty("type").GetString()!, o)).ToList());
    }

    /// <summary>Every outcome of one type.</summary>
    public IEnumerable<ExpectedOutcome> Outcomes(string type) =>
        ExpectedOutcomes.Where(o => string.Equals(o.Type, type, StringComparison.Ordinal));
}
