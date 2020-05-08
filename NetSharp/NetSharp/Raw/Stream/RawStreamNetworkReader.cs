﻿using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace NetSharp.Raw.Stream
{
    public sealed class RawStreamNetworkReader : RawNetworkReaderBase
    {
        /// <inheritdoc />
        public RawStreamNetworkReader(ref Socket rawConnection, NetworkRequestHandler? requestHandler, EndPoint defaultEndPoint, int pooledPacketBufferSize,
            int pooledBuffersPerBucket = 1000, uint preallocatedStateObjects = 0) : base(ref rawConnection, defaultEndPoint, requestHandler, pooledPacketBufferSize,
            pooledBuffersPerBucket, preallocatedStateObjects)
        {
        }

        private void CloseClientConnection(SocketAsyncEventArgs args)
        {
            byte[] rentedBuffer = args.Buffer;
            BufferPool.Return(rentedBuffer, true);

            Socket clientSocket = args.AcceptSocket;

            clientSocket.Shutdown(SocketShutdown.Both);
            clientSocket.Close();
            clientSocket.Dispose();

            ArgsPool.Return(args);
        }

        private void CompleteAccept(SocketAsyncEventArgs args)
        {
            switch (args.SocketError)
            {
                case SocketError.Success:
                    StartReceive(args);
                    break;

                case SocketError.ConnectionReset:
                    /*
                     * The SocketAsyncEventArgs.Completed event can occur in some cases when no connection has been accepted and cause the SocketAsyncEventArgs.SocketError property to be set to ConnectionReset.
                     * This can occur as a result of port scanning using a half-open SYN type scan (a SYN -> SYN-ACK -> RST sequence).
                     * Applications using the AcceptAsync method should be prepared to handle this condition.
                     */
                    ArgsPool.Return(args);
                    break;

                default:
                    ArgsPool.Return(args);
                    break;
            }

            StartDefaultAccept();
        }

        private void CompleteReceive(SocketAsyncEventArgs args)
        {
            TransmissionToken token = (TransmissionToken)args.UserToken;

            byte[] receiveBuffer = args.Buffer;
            int expectedBytes = receiveBuffer.Length;

            switch (args.SocketError)
            {
                case SocketError.Success:
                    int receivedBytes = args.BytesTransferred, totalReceivedBytes = token.BytesTransferred;

                    if (totalReceivedBytes + receivedBytes == expectedBytes)  // transmission complete
                    {
                        byte[] responseBufferHandle = BufferPool.Rent(expectedBytes);

                        bool responseExists =
                            RequestHandler(args.AcceptSocket.RemoteEndPoint, receiveBuffer, totalReceivedBytes + receivedBytes, responseBufferHandle);
                        BufferPool.Return(receiveBuffer, true);

                        if (responseExists)
                        {
                            args.SetBuffer(responseBufferHandle, 0, PacketBufferSize);

                            TransmissionToken sendToken = new TransmissionToken(0);
                            args.UserToken = sendToken;

                            StartSend(args);
                            return;
                        }

                        BufferPool.Return(responseBufferHandle, true);

                        StartReceive(args);
                    }
                    else if (0 < totalReceivedBytes + receivedBytes && totalReceivedBytes + receivedBytes < expectedBytes)  // transmission not complete
                    {
                        token = new TransmissionToken(in token, args.BytesTransferred);
                        args.UserToken = token;

                        args.SetBuffer(totalReceivedBytes, expectedBytes - receivedBytes);

                        ContinueReceive(args);
                    }
                    else if (receivedBytes == 0)  // connection is dead
                    {
                        CloseClientConnection(args);
                    }
                    break;

                default:
                    CloseClientConnection(args);
                    break;
            }
        }

        private void CompleteSend(SocketAsyncEventArgs args)
        {
            TransmissionToken token = (TransmissionToken)args.UserToken;

            byte[] sendBuffer = args.Buffer;
            int expectedBytes = sendBuffer.Length;

            switch (args.SocketError)
            {
                case SocketError.Success:
                    int sentBytes = args.BytesTransferred, totalSentBytes = token.BytesTransferred;

                    if (totalSentBytes + sentBytes == expectedBytes)  // transmission complete
                    {
                        BufferPool.Return(sendBuffer, true);

                        StartReceive(args);
                    }
                    else if (0 < totalSentBytes + sentBytes && totalSentBytes + sentBytes < expectedBytes)  // transmission not complete
                    {
                        token = new TransmissionToken(in token, args.BytesTransferred);
                        args.UserToken = token;

                        args.SetBuffer(totalSentBytes, expectedBytes - sentBytes);

                        ContinueSend(args);
                    }
                    else if (sentBytes == 0)  // connection is dead
                    {
                        CloseClientConnection(args);
                    }
                    break;

                default:
                    CloseClientConnection(args);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ContinueReceive(SocketAsyncEventArgs args)
        {
            if (ShutdownToken.IsCancellationRequested)
            {
                CloseClientConnection(args);
                return;
            }

            Socket clientSocket = args.AcceptSocket;

            if (clientSocket.ReceiveAsync(args)) return;

            CompleteReceive(args);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ContinueSend(SocketAsyncEventArgs args)
        {
            if (ShutdownToken.IsCancellationRequested)
            {
                CloseClientConnection(args);
                return;
            }

            Socket clientSocket = args.AcceptSocket;

            if (clientSocket.SendAsync(args)) return;

            CompleteSend(args);
        }

        private void HandleIoCompleted(object sender, SocketAsyncEventArgs args)
        {
            switch (args.LastOperation)
            {
                case SocketAsyncOperation.Accept:
                    StartDefaultAccept();
                    CompleteAccept(args);
                    break;

                case SocketAsyncOperation.Send:
                    CompleteSend(args);
                    break;

                case SocketAsyncOperation.Receive:
                    CompleteReceive(args);
                    break;
            }
        }

        private void StartAccept(SocketAsyncEventArgs args)
        {
            if (ShutdownToken.IsCancellationRequested)
            {
                return;
            }

            if (Connection.AcceptAsync(args)) return;

            StartDefaultAccept();
            CompleteAccept(args);
        }

        private void StartDefaultAccept()
        {
            if (ShutdownToken.IsCancellationRequested)
            {
                return;
            }

            SocketAsyncEventArgs args = ArgsPool.Rent();
            StartAccept(args);
        }

        private void StartReceive(SocketAsyncEventArgs args)
        {
            if (ShutdownToken.IsCancellationRequested)
            {
                CloseClientConnection(args);
                return;
            }

            Socket clientSocket = args.AcceptSocket;

            byte[] receiveBuffer = BufferPool.Rent(PacketBufferSize);

            args.SetBuffer(receiveBuffer, 0, PacketBufferSize);

            TransmissionToken token = new TransmissionToken(0);
            args.UserToken = token;

            if (clientSocket.ReceiveAsync(args)) return;

            CompleteReceive(args);
        }

        private void StartSend(SocketAsyncEventArgs args)
        {
            if (ShutdownToken.IsCancellationRequested)
            {
                CloseClientConnection(args);
                return;
            }

            Socket clientSocket = args.AcceptSocket;

            if (clientSocket.SendAsync(args)) return;

            CompleteSend(args);
        }

        /// <inheritdoc />
        protected override bool CanReuseStateObject(ref SocketAsyncEventArgs instance)
        {
            return true;
        }

        /// <inheritdoc />
        protected override SocketAsyncEventArgs CreateStateObject()
        {
            SocketAsyncEventArgs args = new SocketAsyncEventArgs();
            args.Completed += HandleIoCompleted;

            return args;
        }

        /// <inheritdoc />
        protected override void DestroyStateObject(SocketAsyncEventArgs instance)
        {
            instance.Completed -= HandleIoCompleted;
            instance.Dispose();
        }

        /// <inheritdoc />
        protected override void ResetStateObject(ref SocketAsyncEventArgs instance)
        {
            instance.AcceptSocket = null;
        }

        /// <inheritdoc />
        public override void Start(ushort concurrentReadTasks)
        {
            for (ushort i = 0; i < concurrentReadTasks; i++)
            {
                StartDefaultAccept();
            }
        }

        private readonly struct TransmissionToken
        {
            public readonly int BytesTransferred;

            public TransmissionToken(int bytesTransferred)
            {
                BytesTransferred = bytesTransferred;
            }

            public TransmissionToken(in TransmissionToken token, int newlyTransferredBytes)
            {
                BytesTransferred = token.BytesTransferred + newlyTransferredBytes;
            }
        }
    }
}