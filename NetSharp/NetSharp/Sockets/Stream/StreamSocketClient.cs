﻿using NetSharp.Packets;
using NetSharp.Utils;

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetSharp.Sockets.Stream
{
    //TODO document
    public readonly struct StreamSocketClientOptions
    {
        public static readonly StreamSocketClientOptions Defaults =
            new StreamSocketClientOptions(NetworkPacket.TotalSize);

        public readonly int PacketSize;

        public StreamSocketClientOptions(int packetSize)
        {
            PacketSize = packetSize;
        }
    }

    //TODO address the need to handle series of network packets, not just single packets
    //TODO document class
    public sealed class StreamSocketClient : SocketClient
    {
        public StreamSocketClient(in AddressFamily connectionAddressFamily, in ProtocolType connectionProtocolType,
            in StreamSocketClientOptions? clientOptions = null) : base(in connectionAddressFamily, SocketType.Stream,
            in connectionProtocolType, clientOptions?.PacketSize ?? StreamSocketClientOptions.Defaults.PacketSize)
        {
        }

        /// <inheritdoc />
        protected override SocketAsyncEventArgs CreateTransmissionArgs()
        {
            SocketAsyncEventArgs connectionArgs = new SocketAsyncEventArgs();

            connectionArgs.Completed += HandleIoCompleted;

            return connectionArgs;
        }

        /// <inheritdoc />
        protected override void ResetTransmissionArgs(SocketAsyncEventArgs args)
        {
        }

        /// <inheritdoc />
        protected override bool CanTransmissionArgsBeReused(in SocketAsyncEventArgs args)
        {
            return true;
        }

        /// <inheritdoc />
        protected override void DestroyTransmissionArgs(SocketAsyncEventArgs remoteConnectionArgs)
        {
            remoteConnectionArgs.Completed -= HandleIoCompleted;

            remoteConnectionArgs.Dispose();
        }

        /// <inheritdoc />
        protected override void HandleIoCompleted(object sender, SocketAsyncEventArgs args)
        {
            switch (args.LastOperation)
            {
                case SocketAsyncOperation.Connect:
                    AsyncOperationToken connectToken = (AsyncOperationToken)args.UserToken;

                    if (connectToken.CancellationToken.IsCancellationRequested)
                    {
                        Connection.Disconnect(true);

                        connectToken.CompletionSource.SetCanceled();
                    }
                    else if (args.SocketError == SocketError.Success)
                    {
                        connectToken.CompletionSource.SetResult(true);
                    }
                    else
                    {
                        connectToken.CompletionSource.SetException(new SocketException((int)args.SocketError));
                    }

                    TransmissionArgsPool.Return(args);

                    break;

                case SocketAsyncOperation.Disconnect:
                    AsyncOperationToken disconnectToken = (AsyncOperationToken)args.UserToken;

                    if (disconnectToken.CancellationToken.IsCancellationRequested)
                    {
                        disconnectToken.CompletionSource.SetCanceled();
                    }
                    else if (args.SocketError == SocketError.Success)
                    {
                        disconnectToken.CompletionSource.SetResult(true);
                    }
                    else
                    {
                        disconnectToken.CompletionSource.SetException(new SocketException((int)args.SocketError));
                    }

                    TransmissionArgsPool.Return(args);

                    break;

                case SocketAsyncOperation.Receive:
                    AsyncTransmissionToken receiveToken = (AsyncTransmissionToken)args.UserToken;

                    if (receiveToken.CancellationToken.IsCancellationRequested)
                    {
                        receiveToken.CompletionSource.SetCanceled();

                        TransmissionArgsPool.Return(args);
                    }
                    else if (args.SocketError == SocketError.Success)
                    {
                        Memory<byte> transmissionBuffer = args.MemoryBuffer;
                        int expectedBytes = transmissionBuffer.Length;

                        if (args.BytesTransferred == expectedBytes)
                        {
                            // buffer was fully received

                            TransmissionResult result = new TransmissionResult(in args);

                            receiveToken.CompletionSource.SetResult(result);

                            TransmissionArgsPool.Return(args);
                        }
                        else if (expectedBytes > args.BytesTransferred && args.BytesTransferred > 0)
                        {
                            // receive the remaining parts of the buffer

                            int receivedBytes = args.BytesTransferred;

                            args.SetBuffer(receivedBytes, expectedBytes - receivedBytes);

                            Connection.ReceiveAsync(args);
                        }
                        else
                        {
                            // no bytes were received, remote socket is dead

                            receiveToken.CompletionSource.SetException(new SocketException((int)SocketError.HostDown));

                            TransmissionArgsPool.Return(args);
                        }
                    }
                    else
                    {
                        receiveToken.CompletionSource.SetException(new SocketException((int)args.SocketError));

                        TransmissionArgsPool.Return(args);
                    }

                    break;

                case SocketAsyncOperation.Send:
                    AsyncTransmissionToken sendToken = (AsyncTransmissionToken)args.UserToken;

                    if (sendToken.CancellationToken.IsCancellationRequested)
                    {
                        sendToken.CompletionSource.SetCanceled();

                        TransmissionArgsPool.Return(args);
                    }
                    else if (args.SocketError == SocketError.Success)
                    {
                        Memory<byte> transmissionBuffer = args.MemoryBuffer;
                        int remainingBytes = transmissionBuffer.Length;

                        if (args.BytesTransferred == remainingBytes)
                        {
                            // buffer was fully sent

                            TransmissionResult result = new TransmissionResult(in args);

                            sendToken.CompletionSource.SetResult(result);

                            TransmissionArgsPool.Return(args);
                        }
                        else if (remainingBytes > args.BytesTransferred && args.BytesTransferred > 0)
                        {
                            // send the remaining parts of the buffer

                            int sentBytes = args.BytesTransferred;

                            args.SetBuffer(sentBytes, remainingBytes - sentBytes);

                            Connection.SendAsync(args);
                        }
                        else
                        {
                            // no bytes were sent, remote socket is dead

                            sendToken.CompletionSource.SetException(new SocketException((int)SocketError.HostDown));

                            TransmissionArgsPool.Return(args);
                        }
                    }
                    else
                    {
                        sendToken.CompletionSource.SetException(new SocketException((int)args.SocketError));

                        TransmissionArgsPool.Return(args);
                    }

                    break;

                default:
                    throw new NotSupportedException($"{nameof(HandleIoCompleted)} doesn't support {args.LastOperation}");
            }
        }

        public void Disconnect(bool allowSocketReuse)
        {
            Connection.Disconnect(allowSocketReuse);
        }

        public ValueTask DisconnectAsync(bool allowSocketReuse, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();

            SocketAsyncEventArgs args = TransmissionArgsPool.Rent();

            args.DisconnectReuseSocket = allowSocketReuse;
            args.UserToken = new AsyncOperationToken(in tcs, in cancellationToken);

            if (Connection.DisconnectAsync(args)) return new ValueTask(tcs.Task);

            TransmissionArgsPool.Return(args);

            return new ValueTask();
        }

        public TransmissionResult Receive(byte[] buffer, SocketFlags flags = SocketFlags.None)
        {
            int receivedBytes = Connection.Receive(buffer, flags);

            return new TransmissionResult(in buffer, in receivedBytes, Connection.RemoteEndPoint);
        }

        public ValueTask<TransmissionResult> ReceiveAsync(Memory<byte> receiveBuffer, SocketFlags flags = SocketFlags.None,
            CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<TransmissionResult> tcs = new TaskCompletionSource<TransmissionResult>();

            SocketAsyncEventArgs args = TransmissionArgsPool.Rent();

            args.SetBuffer(receiveBuffer);

            args.SocketFlags = flags;
            args.UserToken = new AsyncTransmissionToken(in tcs, in cancellationToken);

            if (Connection.ReceiveAsync(args)) return new ValueTask<TransmissionResult>(tcs.Task);

            TransmissionResult result = new TransmissionResult(in args);

            TransmissionArgsPool.Return(args);

            return new ValueTask<TransmissionResult>(result);
        }

        public TransmissionResult Send(byte[] buffer, SocketFlags flags = SocketFlags.None)
        {
            int sentBytes = Connection.Send(buffer, flags);

            return new TransmissionResult(in buffer, in sentBytes, Connection.RemoteEndPoint);
        }

        public ValueTask<TransmissionResult> SendAsync(Memory<byte> sendBuffer, SocketFlags flags = SocketFlags.None,
            CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<TransmissionResult> tcs = new TaskCompletionSource<TransmissionResult>();

            SocketAsyncEventArgs args = TransmissionArgsPool.Rent();

            args.SetBuffer(sendBuffer);

            args.SocketFlags = flags;
            args.UserToken = new AsyncTransmissionToken(in tcs, in cancellationToken);

            if (Connection.SendToAsync(args)) return new ValueTask<TransmissionResult>(tcs.Task);

            TransmissionResult result = new TransmissionResult(in args);

            TransmissionArgsPool.Return(args);

            return new ValueTask<TransmissionResult>(result);
        }
    }
}