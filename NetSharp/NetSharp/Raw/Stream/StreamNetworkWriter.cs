﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace NetSharp.Raw.Stream
{
    public sealed class StreamNetworkWriter : NetworkWriterBase<SocketAsyncEventArgs>
    {
        /// <inheritdoc />
        public StreamNetworkWriter(ref Socket rawConnection, EndPoint defaultEndPoint, int maxPooledBufferSize, int maxPooledBuffersPerBucket = 1000,
            uint preallocatedStateObjects = 0) : base(ref rawConnection, defaultEndPoint, maxPooledBufferSize, maxPooledBuffersPerBucket, preallocatedStateObjects)
        {
        }

        private void CompleteConnect(SocketAsyncEventArgs args)
        {
            throw new NotImplementedException();
        }

        private void CompleteDisconnect(SocketAsyncEventArgs args)
        {
            throw new NotImplementedException();
        }

        private void CompleteReceive(SocketAsyncEventArgs args)
        {
            AsyncStreamReadToken token = (AsyncStreamReadToken)args.UserToken;

            byte[] receiveBufferHandle = args.Buffer;
            int expectedBytes = receiveBufferHandle.Length;

            switch (args.SocketError)
            {
                case SocketError.Success:
                    int receivedBytes = args.BytesTransferred, totalReceivedBytes = token.TotalReadBytes;

                    if (receivedBytes == 0)  // connection is dead
                    {
                        token.CompletionSource.SetException(new SocketException((int)SocketError.HostDown));
                    }
                    else if (0 < totalReceivedBytes + receivedBytes && totalReceivedBytes + receivedBytes < expectedBytes)  // transmission not complete
                    {
                        // update user token to take account of newly read bytes
                        token = new AsyncStreamReadToken(in token, receivedBytes);
                        args.UserToken = token;

                        args.SetBuffer(totalReceivedBytes, expectedBytes - receivedBytes);

                        ContinueReceive(args);
                        return;
                    }
                    else if (totalReceivedBytes + receivedBytes == expectedBytes)  // transmission complete
                    {
                        receiveBufferHandle.CopyTo(token.UserBuffer);
                        token.CompletionSource.SetResult(args.BytesTransferred);
                    }
                    break;

                case SocketError.OperationAborted:
                    token.CompletionSource.SetCanceled();
                    break;

                default:
                    int errorCode = (int)args.SocketError;
                    token.CompletionSource.SetException(new SocketException(errorCode));
                    break;
            }

            BufferPool.Return(receiveBufferHandle, true);
            StateObjectPool.Return(args);
        }

        private void CompleteSend(SocketAsyncEventArgs args)
        {
            AsyncStreamWriteToken token = (AsyncStreamWriteToken)args.UserToken;

            byte[] sendBufferHandle = args.Buffer;
            int expectedBytes = sendBufferHandle.Length;

            switch (args.SocketError)
            {
                case SocketError.Success:
                    int sentBytes = args.BytesTransferred, totalSentBytes = token.TotalWrittenBytes;

                    if (sentBytes == 0)  // connection is dead
                    {
                        token.CompletionSource.SetException(new SocketException((int)SocketError.HostDown));
                    }
                    else if (0 < totalSentBytes + sentBytes && totalSentBytes + sentBytes < expectedBytes)  // transmission not complete
                    {
                        // update user token to take account of newly written bytes
                        token = new AsyncStreamWriteToken(in token, sentBytes);
                        args.UserToken = token;

                        args.SetBuffer(totalSentBytes, expectedBytes - sentBytes);

                        ContinueSend(args);
                        return;
                    }
                    else if (totalSentBytes + sentBytes == expectedBytes)  // transmission complete
                    {
                        token.CompletionSource.SetResult(args.BytesTransferred);
                    }
                    break;

                case SocketError.OperationAborted:
                    token.CompletionSource.SetCanceled();
                    break;

                default:
                    int errorCode = (int)args.SocketError;
                    token.CompletionSource.SetException(new SocketException(errorCode));
                    break;
            }

            BufferPool.Return(sendBufferHandle, true);
            StateObjectPool.Return(args);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ContinueReceive(SocketAsyncEventArgs args)
        {
            if (Connection.ReceiveAsync(args)) return;

            CompleteReceive(args);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ContinueSend(SocketAsyncEventArgs args)
        {
            if (Connection.SendAsync(args)) return;

            CompleteSend(args);
        }

        private void HandleIoCompleted(object sender, SocketAsyncEventArgs args)
        {
            switch (args.LastOperation)
            {
                case SocketAsyncOperation.Connect:
                    CompleteConnect(args);
                    break;

                case SocketAsyncOperation.Disconnect:
                    CompleteDisconnect(args);
                    break;

                case SocketAsyncOperation.Receive:
                    CompleteReceive(args);
                    break;

                case SocketAsyncOperation.Send:
                    CompleteSend(args);
                    break;
            }
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
        }

        /// <inheritdoc />
        public override int Read(ref EndPoint remoteEndPoint, Memory<byte> readBuffer, SocketFlags flags = SocketFlags.None)
        {
            int totalBytes = readBuffer.Length;
            if (totalBytes > BufferSize)
            {
                throw new ArgumentException(
                    $"Cannot rent a temporary buffer of size: {totalBytes} bytes; maximum temporary buffer size: {BufferSize} bytes",
                    nameof(readBuffer.Length)
                );
            }

            int readBytes = 0;

            byte[] transmissionBuffer = BufferPool.Rent(totalBytes);

            do
            {
                readBytes += Connection.Receive(transmissionBuffer, readBytes, totalBytes - readBytes, flags);
            } while (readBytes < totalBytes && readBytes != 0);

            transmissionBuffer.CopyTo(readBuffer);
            BufferPool.Return(transmissionBuffer, true);

            return readBytes;
        }

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(EndPoint remoteEndPoint, Memory<byte> readBuffer, SocketFlags flags = SocketFlags.None)
        {
            int totalBytes = readBuffer.Length;
            if (totalBytes > BufferSize)
            {
                throw new ArgumentException(
                    $"Cannot rent a temporary buffer of size: {totalBytes} bytes; maximum temporary buffer size: {BufferSize} bytes",
                    nameof(readBuffer.Length)
                );
            }

            TaskCompletionSource<int> tcs = new TaskCompletionSource<int>();
            SocketAsyncEventArgs args = StateObjectPool.Rent();

            byte[] transmissionBuffer = BufferPool.Rent(totalBytes);

            args.SetBuffer(transmissionBuffer, 0, BufferSize);

            args.RemoteEndPoint = remoteEndPoint;
            args.SocketFlags = flags;

            AsyncStreamReadToken token = new AsyncStreamReadToken(tcs, 0, in readBuffer);
            args.UserToken = token;

            if (!Connection.ReceiveAsync(args)) CompleteReceive(args);

            return new ValueTask<int>(tcs.Task);
        }

        /// <inheritdoc />
        public override int Write(EndPoint remoteEndPoint, ReadOnlyMemory<byte> writeBuffer, SocketFlags flags = SocketFlags.None)
        {
            int totalBytes = writeBuffer.Length;
            if (totalBytes > BufferSize)
            {
                throw new ArgumentException(
                    $"Cannot rent a temporary buffer of size: {totalBytes} bytes; maximum temporary buffer size: {BufferSize} bytes",
                    nameof(writeBuffer.Length)
                );
            }

            int writtenBytes = 0;

            byte[] transmissionBuffer = BufferPool.Rent(totalBytes);
            writeBuffer.CopyTo(transmissionBuffer);

            do
            {
                writtenBytes += Connection.Send(transmissionBuffer, writtenBytes, totalBytes - writtenBytes, flags);
            } while (writtenBytes < totalBytes && writtenBytes != 0);

            BufferPool.Return(transmissionBuffer);

            return writtenBytes;
        }

        /// <inheritdoc />
        public override ValueTask<int> WriteAsync(EndPoint remoteEndPoint, ReadOnlyMemory<byte> writeBuffer, SocketFlags flags = SocketFlags.None)
        {
            int totalBytes = writeBuffer.Length;
            if (totalBytes > BufferSize)
            {
                throw new ArgumentException(
                    $"Cannot rent a temporary buffer of size: {totalBytes} bytes; maximum temporary buffer size: {BufferSize} bytes",
                    nameof(writeBuffer.Length)
                );
            }

            TaskCompletionSource<int> tcs = new TaskCompletionSource<int>();
            SocketAsyncEventArgs args = StateObjectPool.Rent();

            byte[] transmissionBuffer = BufferPool.Rent(totalBytes);
            writeBuffer.CopyTo(transmissionBuffer);

            args.SetBuffer(transmissionBuffer, 0, BufferSize);

            args.RemoteEndPoint = remoteEndPoint;
            args.SocketFlags = flags;

            AsyncStreamWriteToken token = new AsyncStreamWriteToken(tcs, 0);
            args.UserToken = token;

            if (!Connection.SendAsync(args)) CompleteSend(args);

            return new ValueTask<int>(tcs.Task);
        }

        private readonly struct AsyncStreamReadToken
        {
            public readonly TaskCompletionSource<int> CompletionSource;
            public readonly int TotalReadBytes;
            public readonly Memory<byte> UserBuffer;

            public AsyncStreamReadToken(TaskCompletionSource<int> completionSource, int totalReadBytes, in Memory<byte> userBuffer)
            {
                CompletionSource = completionSource;

                TotalReadBytes = totalReadBytes;

                UserBuffer = userBuffer;
            }

            public AsyncStreamReadToken(in AsyncStreamReadToken previousToken, int newlyReadBytes)
            {
                CompletionSource = previousToken.CompletionSource;

                TotalReadBytes = previousToken.TotalReadBytes + newlyReadBytes;

                UserBuffer = previousToken.UserBuffer;
            }
        }

        private readonly struct AsyncStreamWriteToken
        {
            public readonly TaskCompletionSource<int> CompletionSource;
            public readonly int TotalWrittenBytes;

            public AsyncStreamWriteToken(TaskCompletionSource<int> completionSource, int totalWrittenBytes)
            {
                CompletionSource = completionSource;

                TotalWrittenBytes = totalWrittenBytes;
            }

            public AsyncStreamWriteToken(in AsyncStreamWriteToken previousToken, int newlyWrittenBytes)
            {
                CompletionSource = previousToken.CompletionSource;

                TotalWrittenBytes = previousToken.TotalWrittenBytes + newlyWrittenBytes;
            }
        }
    }
}