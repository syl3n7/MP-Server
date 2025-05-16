using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Numerics;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole();
var host = builder.Build();

var server = new RacingServer(7777, 7778);
await server.StartAsync();

Console.WriteLine("Server started! Press Ctrl+C to stop...");
await host.RunAsync();