using System;
using System.Net;
using System.Numerics;

namespace MP.Server
{
    public class PlayerInfo
    {
        public string Id { get; }
        public string Name { get; }
        public IPEndPoint? UdpEndpoint { get; }
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }

        public PlayerInfo(string id, string name, IPEndPoint? udpEndpoint, Vector3 position, Quaternion rotation)
        {
            Id = id;
            Name = name;
            UdpEndpoint = udpEndpoint;
            Position = position;
            Rotation = rotation;
        }
    }
}