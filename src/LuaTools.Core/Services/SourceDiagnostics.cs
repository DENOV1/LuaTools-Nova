// LuaTools Nova — turns raw source-discovery outcomes into honest, distinct user-facing messages.
// v1.0.3 bug 3: every failure used to read "No available sources found for this game" — even
// backend outages, sign-in requirements and network errors. The original silently returned an
// empty dictionary on HTTP failure (LuaToolsApiClient.CheckSourcesAsync), which made a dead
// manifest backend indistinguishable from a game that genuinely has no lua sources.
namespace LuaTools.Core.Services;

public static class SourceDiagnostics
{
    public sealed record Outcome(string Message, bool HadFailure);

    /// <summary>Classify a completed source check for user display.</summary>
    /// <param name="statuses">Raw name→status strings from the manifest backend (may be empty).</param>
    /// <param name="backendFailed">True when the check itself failed (HTTP error / network / parse).</param>
    /// <param name="anyRowCanDownload">True if at least one source row is downloadable right now.</param>
    /// <param name="anyRowGated">True if rows exist but every available one needs sign-in / a key / supporter status.</param>
    public static Outcome Classify(
        IReadOnlyDictionary<string, string> statuses,
        bool backendFailed,
        bool anyRowCanDownload,
        bool anyRowGated)
    {
        if (backendFailed)
            return new Outcome("The manifest source backend could not be reached. Check your connection and try again — this is not the same as the game having no sources.", true);

        if (statuses.Count == 0)
            return new Outcome("The source backend responded but listed no sources for this game. It may have no lua available yet.", false);

        if (anyRowCanDownload)
            return new Outcome("", false); // normal path — no error shown

        if (anyRowGated)
            return new Outcome("Sources exist for this game, but all of them require signing in (Discord) or a Hubcap key. Sign in on the Home page or add a Hubcap key in Settings.", false);

        return new Outcome("No source is currently available for this game (sources were reachable, but reported errors). This may be temporary.", false);
    }
}
