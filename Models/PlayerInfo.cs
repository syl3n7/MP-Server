using System.Net;
using System.Numerics;

public record PlayerInfo(
    string Id,
    string Name,
    IPEndPoint? UdpEndpoint,
    Vector3 Position,
    Quaternion Rotation
);