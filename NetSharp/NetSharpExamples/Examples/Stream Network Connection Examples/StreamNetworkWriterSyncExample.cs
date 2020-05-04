﻿using NetSharp.Raw.Stream;

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace NetSharpExamples.Examples.Stream_Network_Connection_Examples
{
    public class StreamNetworkWriterSyncExample : INetSharpExample
    {
        private const int PacketSize = 8192;

        public static readonly EndPoint ClientEndPoint = new IPEndPoint(IPAddress.Loopback, 0);

        public static readonly EndPoint ServerEndPoint = StreamNetworkReaderExample.ServerEndPoint;

        /// <inheritdoc />
        public string Name { get; } = "Stream Network Writer Example (Synchronous)";

        /// <inheritdoc />
        public Task RunAsync()
        {
            EndPoint defaultEndPoint = new IPEndPoint(IPAddress.Any, 0);

            Socket rawSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            rawSocket.Bind(ClientEndPoint);

            using StreamNetworkWriter writer = new StreamNetworkWriter(ref rawSocket, defaultEndPoint, PacketSize);
            writer.Connect(ServerEndPoint);

            byte[] transmissionBuffer = new byte[PacketSize];

            EndPoint remoteEndPoint = ServerEndPoint;

            while (true)
            {
                int sent = writer.Write(remoteEndPoint, transmissionBuffer);

                lock (typeof(Console))
                {
                    Console.WriteLine($"Sent {sent} bytes to {remoteEndPoint}!");
                }

                int received = writer.Read(ref remoteEndPoint, transmissionBuffer);

                lock (typeof(Console))
                {
                    Console.WriteLine($"Received {received} bytes from {remoteEndPoint}!");
                }
            }

            writer.Disconnect(false);

            rawSocket.Close();
            rawSocket.Dispose();

            return Task.CompletedTask;
        }
    }
}