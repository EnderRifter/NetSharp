﻿using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetSharp.Utils;

namespace NetSharp.Sockets
{
    //TODO document class
    public abstract class SocketClient : SocketConnection
    {
        //TODO document
        protected readonly struct AsyncTransmissionToken
        {
            public readonly TaskCompletionSource<TransmissionResult> CompletionSource;

            public readonly CancellationToken CancellationToken;

            public AsyncTransmissionToken(in TaskCompletionSource<TransmissionResult> completionSource, in CancellationToken cancellationToken)
            {
                CompletionSource = completionSource;

                CancellationToken = cancellationToken;
            }
        }

        //TODO document
        protected readonly struct AsyncOperationToken
        {
            public readonly TaskCompletionSource<bool> CompletionSource;

            public readonly CancellationToken CancellationToken;

            public AsyncOperationToken(in TaskCompletionSource<bool> completionSource, in CancellationToken cancellationToken)
            {
                CompletionSource = completionSource;

                CancellationToken = cancellationToken;
            }
        }

        protected SocketClient(in AddressFamily connectionAddressFamily, in SocketType connectionSocketType, in ProtocolType connectionProtocolType)
            : base(in connectionAddressFamily, in connectionSocketType, in connectionProtocolType)
        {
        }

        public void Connect(in EndPoint remoteEndPoint)
        {
            connection.Connect(remoteEndPoint);
        }

        public ValueTask ConnectAsync(in EndPoint remoteEndPoint, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();

            SocketAsyncEventArgs args = TransmissionArgsPool.Rent();

            args.RemoteEndPoint = remoteEndPoint;
            args.UserToken = new AsyncOperationToken(in tcs, in cancellationToken);

            if (connection.ConnectAsync(args)) return new ValueTask(tcs.Task);

            TransmissionArgsPool.Return(args);

            return new ValueTask();
        }
    }
}