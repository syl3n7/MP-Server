using System;

namespace MP.Server.Models.Dtos;

/// <summary>Dashboard-facing projection of a SecurityLog row.</summary>
public record SecurityLogDto(
    DateTime Timestamp,
    string   EventType,
    string   IpAddress,
    int      Severity,
    bool     IsResolved
);

/// <summary>Dashboard-facing projection of a ConnectionLog row.</summary>
public record ConnectionLogDto(
    DateTime Timestamp,
    string   EventType,
    string   IpAddress,
    string?  PlayerName,
    bool     UsedTls
);

/// <summary>Dashboard-facing projection of a ServerLog row.</summary>
public record ServerLogDto(
    DateTime Timestamp,
    string   Level,
    string   Category,
    string   Message
);
