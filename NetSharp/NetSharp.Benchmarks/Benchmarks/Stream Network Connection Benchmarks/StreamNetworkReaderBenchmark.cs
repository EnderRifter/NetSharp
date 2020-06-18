﻿using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using NetSharp.Packets;
using NetSharp.Raw.Stream;

namespace NetSharp.Benchmarks.Benchmarks.Stream_Network_Connection_Benchmarks
{
    internal class StreamNetworkReaderBenchmark : INetSharpBenchmark
    {
        private const int PacketSize = 8192, PacketCount = 1_000_000, ClientCount = 12;
        private static readonly ManualResetEventSlim ServerReadyEvent = new ManualResetEventSlim();
        private double[] ClientBandwidths;

        /// <inheritdoc />
        public string Name { get; } = "Raw Stream Network Reader Benchmark";

        private static bool RequestHandler(EndPoint remoteEndPoint, in ReadOnlyMemory<byte> requestBuffer, int receivedRequestBytes,
            in Memory<byte> responseBuffer)
        {
            requestBuffer.CopyTo(responseBuffer);

            return true;
        }

        private Task BenchmarkClientTask(object idObj)
        {
            int id = (int) idObj;

            BenchmarkHelper benchmarkHelper = new BenchmarkHelper();

            Socket clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            clientSocket.Bind(Program.Constants.ClientEndPoint);

            ServerReadyEvent.Wait();
            clientSocket.Connect(Program.Constants.ServerEndPoint);

            byte[] sendBuffer = new byte[PacketSize + RawStreamPacketHeader.TotalSize];
            byte[] receiveBuffer = new byte[PacketSize + RawStreamPacketHeader.TotalSize];
            byte[] packetBuffer = new byte[PacketSize];

            EndPoint remoteEndPoint = Program.Constants.ServerEndPoint;

            lock (typeof(Console))
            {
                Console.WriteLine($"[Client {id}] Starting client; sending messages to {remoteEndPoint}");
            }

            for (int i = 0; i < PacketCount; i++)
            {
                Program.Constants.ServerEncoding.GetBytes($"[Client {id}] Hello World! (Packet {i})").CopyTo(packetBuffer, 0);

                RawStreamPacketHeader streamPacketHeader = new RawStreamPacketHeader(PacketSize);
                RawStreamPacket.Serialise(sendBuffer, in streamPacketHeader, packetBuffer);

                benchmarkHelper.StartStopwatch();

                int totalSent = 0;
                do
                {
                    totalSent += clientSocket.Send(sendBuffer, totalSent, sendBuffer.Length - totalSent,
                        SocketFlags.None);
                } while (totalSent != 0 && totalSent < sendBuffer.Length);

                if (totalSent == 0)
                {
                    break;
                }

                int totalReceived = 0;
                do
                {
                    totalReceived += clientSocket.Receive(receiveBuffer, totalReceived, receiveBuffer.Length - totalReceived,
                        SocketFlags.None);
                } while (totalReceived != 0 && totalReceived < receiveBuffer.Length);

                if (totalReceived == 0)
                {
                    break;
                }

                benchmarkHelper.StopStopwatch();

                benchmarkHelper.SnapshotRttStats();
            }

            clientSocket.Disconnect(true);
            clientSocket.Close();

            benchmarkHelper.PrintBandwidthStats(id, PacketCount, PacketSize);
            benchmarkHelper.PrintRttStats(id);

            ClientBandwidths[id] = benchmarkHelper.CalcBandwidth(PacketCount, PacketSize);

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task RunAsync()
        {
            if (PacketCount > 10_000)
            {
                Console.WriteLine($"{PacketCount} packets will be sent per client. This could take a long time (maybe more than a minute)!");
            }

            ClientBandwidths = new double[ClientCount];
            Task[] clientTasks = new Task[ClientCount];
            for (int i = 0; i < clientTasks.Length; i++)
            {
                clientTasks[i] = Task.Factory.StartNew(BenchmarkClientTask, i, TaskCreationOptions.LongRunning);
            }

            EndPoint defaultEndPoint = new IPEndPoint(IPAddress.Any, 0);

            Socket rawSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            rawSocket.Bind(Program.Constants.ServerEndPoint);
            rawSocket.Listen(ClientCount);

            using RawStreamNetworkReader reader = new RawStreamNetworkReader(ref rawSocket, RequestHandler, defaultEndPoint, PacketSize);
            reader.Start(ClientCount);

            ServerReadyEvent.Set();

            await Task.WhenAll(clientTasks);

            Console.WriteLine($"Total estimated bandwidth: {ClientBandwidths.Sum():F3}");

            reader.Shutdown();

            rawSocket.Close();
            rawSocket.Dispose();
        }
    }
}