using System.Text.Json;

namespace MP.Server.Protocol;

/// <summary>
/// Parsed representation of an inbound TCP message.
/// Handlers read their own fields from <see cref="Raw"/> using JsonElement helpers.
/// </summary>
public sealed class MessageEnvelope
{
    /// <summary>Client-supplied deduplication key. Null if not provided.</summary>
    public string? MessageId { get; init; }

    /// <summary>Lower-case action name (envelope-style routing). Null for legacy command messages.</summary>
    public string? Action { get; init; }

    /// <summary>Upper-case command name (legacy routing). Null for envelope messages.</summary>
    public string? Command { get; init; }

    /// <summary>Full parsed JSON — handlers pull their own fields from this.</summary>
    public JsonElement Raw { get; init; }

    public static MessageEnvelope Parse(JsonElement json)
    {
        string? messageId = json.TryGetProperty("messageId", out var mid) ? mid.GetString() : null;
        string? action    = json.TryGetProperty("action",    out var act) ? act.GetString()?.ToLowerInvariant() : null;
        string? command   = json.TryGetProperty("command",   out var cmd) ? cmd.GetString()?.ToUpperInvariant() : null;

        return new MessageEnvelope
        {
            MessageId = messageId,
            Action    = action,
            Command   = command,
            Raw       = json
        };
    }
}
