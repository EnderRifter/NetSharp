﻿using NetSharp.Utils;

using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace NetSharp.Sockets
{
    /// <summary>
    /// Abstract base class for client and server wrappers around existing <see cref="Socket" /> objects.
    /// </summary>
    public abstract class SocketConnectionBase : IDisposable
    {
        /// <summary>
        /// Pools <see cref="SocketAsyncEventArgs" /> objects for use during network read/write operations and calls to
        /// <see cref="Socket" />.XXXAsync( <see cref="SocketAsyncEventArgs" />) methods.
        /// </summary>
        protected readonly SlimObjectPool<SocketAsyncEventArgs> ArgsPool;

        /// <summary>
        /// Pools arrays to function as temporary buffers during network read/write operations.
        /// </summary>
        protected readonly ArrayPool<byte> BufferPool;

        /// <summary>
        /// The maximum size of buffer that can be rented from the pool.
        /// </summary>
        protected readonly int MaxBufferSize;

        /// <summary>
        /// The underlying <see cref="Socket" /> which provides access to network operations.
        /// </summary>
        protected Socket Connection;

        /// <summary>
        /// Constructs a new instance of the <see cref="SocketConnectionBase" /> class.
        /// </summary>
        /// <param name="rawConnection">
        /// The underlying <see cref="Socket" /> object which should be wrapped by this instance.
        /// </param>
        /// <param name="pooledBufferMaxSize">
        /// The maximum size in bytes of buffers held in the buffer pool.
        /// </param>
        /// <param name="preallocatedTransmissionArgs">
        /// The number of <see cref="SocketAsyncEventArgs" /> objects to initially preallocate.
        /// </param>
        private protected SocketConnectionBase(ref Socket rawConnection, int pooledBufferMaxSize, ushort preallocatedTransmissionArgs)
        {
            Connection = rawConnection;

            MaxBufferSize = pooledBufferMaxSize;
            BufferPool = ArrayPool<byte>.Create(MaxBufferSize, 1000);

            ArgsPool = new SlimObjectPool<SocketAsyncEventArgs>(CreateTransmissionArgs,
                ResetTransmissionArgs, DestroyTransmissionArgs, CanTransmissionArgsBeReused);

            // TODO refactor into a cleaner structure, with a better method of seeding the object pool
            for (ushort i = 0; i < preallocatedTransmissionArgs; i++)
            {
                SocketAsyncEventArgs args = CreateTransmissionArgs();

                ArgsPool.Return(args);
            }
        }

        /// <summary>
        /// The local endpoint to which the underlying <see cref="Socket" /> is bound.
        /// </summary>
        public EndPoint LocalEndPoint
        {
            get { return Connection.LocalEndPoint; }
        }

        /// <summary>
        /// Delegate method used to check whether the given used <see cref="SocketAsyncEventArgs" /> instance can be reused by the
        /// <see cref="ArgsPool" />. If this method returns <c>true</c>, <see cref="ResetTransmissionArgs" /> is called on the given
        /// <paramref name="args" />. Otherwise, <see cref="DestroyTransmissionArgs" /> is called.
        /// </summary>
        /// <param name="args">
        /// The <see cref="SocketAsyncEventArgs" /> instance to check.
        /// </param>
        /// <returns>
        /// Whether the given <paramref name="args" /> should be reset and reused, or should be destroyed.
        /// </returns>
        protected abstract bool CanTransmissionArgsBeReused(ref SocketAsyncEventArgs args);

        /// <summary>
        /// Delegate method used to construct fresh <see cref="SocketAsyncEventArgs" /> instances for use in the <see cref="ArgsPool" />. The
        /// resulting instance should register <see cref="HandleIoCompleted" /> as an event handler for the
        /// <see cref="SocketAsyncEventArgs.Completed" /> event.
        /// </summary>
        /// <returns>
        /// The configured <see cref="SocketAsyncEventArgs" /> instance.
        /// </returns>
        protected abstract SocketAsyncEventArgs CreateTransmissionArgs();

        /// <summary>
        /// Delegate method to destroy used <see cref="SocketAsyncEventArgs" /> instances that cannot be reused by the <see cref="ArgsPool" />. This
        /// method should deregister <see cref="HandleIoCompleted" /> as an event handler for the <see cref="SocketAsyncEventArgs.Completed" /> event.
        /// </summary>
        /// <param name="remoteConnectionArgs">
        /// The <see cref="SocketAsyncEventArgs" /> which should be destroyed.
        /// </param>
        protected abstract void DestroyTransmissionArgs(SocketAsyncEventArgs remoteConnectionArgs);

        /// <summary>
        /// Disposes of managed and unmanaged resources used by the <see cref="SocketConnectionBase" /> class.
        /// </summary>
        /// <param name="disposing">
        /// Whether this call was made by a call to <see cref="Dispose()" />.
        /// </param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            ArgsPool.Dispose();
        }

        /// <summary>
        /// Delegate method to handle asynchronous network IO completion via the <see cref="SocketAsyncEventArgs.Completed" /> event.
        /// </summary>
        /// <param name="sender">
        /// The object which raised the event.
        /// </param>
        /// <param name="args">
        /// The <see cref="SocketAsyncEventArgs" /> instance associated with the asynchronous network IO.
        /// </param>
        protected abstract void HandleIoCompleted(object sender, SocketAsyncEventArgs args);

        /// <summary>
        /// Delegate method used to reset used <see cref="SocketAsyncEventArgs" /> instances for later reuse by the <see cref="ArgsPool" />.
        /// </summary>
        /// <param name="args">
        /// The <see cref="SocketAsyncEventArgs" /> instance that should be reset.
        /// </param>
        protected abstract void ResetTransmissionArgs(ref SocketAsyncEventArgs args);

        /// <summary>
        /// Binds the underlying socket.
        /// </summary>
        /// <param name="localEndPoint">
        /// The end point to which the socket should be bound.
        /// </param>
        public void Bind(in EndPoint localEndPoint)
        {
            Connection.Bind(localEndPoint);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Shuts down the underlying socket.
        /// </summary>
        /// <param name="how">
        /// Which socket transmission functions should be shut down on the socket.
        /// </param>
        public void Shutdown(SocketShutdown how)
        {
            try
            {
                Connection.Shutdown(how);
            }
            catch (SocketException) { }

            Connection.Close();
        }
    }
}