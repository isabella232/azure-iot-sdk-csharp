﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics.Contracts;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Microsoft.Azure.Devices.Client.Extensions;

namespace Microsoft.Azure.Devices.Client.Transport.Mqtt
{
    public sealed class ClientWebSocketChannel : AbstractChannel, IDisposable
    {
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _writeCancellationTokenSource;
        private bool _isActive;
        private bool _isReadPending;
        private bool _isWriteInProgress;

        public ClientWebSocketChannel(IChannel parent, ClientWebSocket webSocket)
            : base(parent)
        {
            _webSocket = webSocket;
            _isActive = true;
            Metadata = new ChannelMetadata(false, 16);
            Configuration = new ClientWebSocketChannelConfig();
            _writeCancellationTokenSource = new CancellationTokenSource();
        }

        public override IChannelConfiguration Configuration { get; }

        public override bool Open => _isActive && _webSocket?.State == WebSocketState.Open;

        public override bool Active => _isActive;

        public override ChannelMetadata Metadata { get; }

        protected override EndPoint LocalAddressInternal { get; }

        protected override EndPoint RemoteAddressInternal { get; }

        protected override IChannelUnsafe NewUnsafe() => new WebSocketChannelUnsafe(this);

        protected override bool IsCompatible(IEventLoop eventLoop) => true;

        public ClientWebSocketChannel Option<T>(ChannelOption<T> option, T value)
        {
            Contract.Requires(option != null);

            Configuration.SetOption(option, value);
            return this;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _webSocket?.Dispose();
            _webSocket = null;

            _writeCancellationTokenSource?.Dispose();
            _writeCancellationTokenSource = null;
        }

        protected class WebSocketChannelUnsafe : AbstractUnsafe
        {
            public WebSocketChannelUnsafe(AbstractChannel channel)
                : base(channel)
            {
            }

            public override Task ConnectAsync(EndPoint remoteAddress, EndPoint localAddress)
            {
                throw new NotSupportedException("ClientWebSocketChannel does not support BindAsync()");
            }

            protected override void Flush0()
            {
                // Flush immediately only when there's no pending flush.
                // If there's a pending flush operation, event loop will call FinishWrite() later,
                // and thus there's no need to call it now.
                if (((ClientWebSocketChannel)channel)._isWriteInProgress)
                {
                    return;
                }

                base.Flush0();
            }
        }

        protected override void DoBind(EndPoint localAddress)
        {
            throw new NotSupportedException("ClientWebSocketChannel does not support DoBind()");
        }

        protected override void DoDisconnect()
        {
            throw new NotSupportedException("ClientWebSocketChannel does not support DoDisconnect()");
        }

        protected override async void DoClose()
        {
            try
            {
                WebSocketState webSocketState = _webSocket.State;
                if (webSocketState != WebSocketState.Closed && webSocketState != WebSocketState.Aborted)
                {
                    // Cancel any pending write
                    CancelPendingWrite();
                    _isActive = false;

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (!e.IsFatal())
            {
                Abort();
            }
        }

        protected override async void DoBeginRead()
        {
            IByteBuffer byteBuffer = null;
            IRecvByteBufAllocatorHandle allocHandle = null;
            bool close = false;
            try
            {
                if (!Open || _isReadPending)
                {
                    return;
                }

                _isReadPending = true;
                IByteBufferAllocator allocator = Configuration.Allocator;
                allocHandle = Configuration.RecvByteBufAllocator.NewHandle();
                allocHandle.Reset(Configuration);
                do
                {
                    byteBuffer = allocHandle.Allocate(allocator);
                    allocHandle.LastBytesRead = await DoReadBytesAsync(byteBuffer).ConfigureAwait(false);
                    if (allocHandle.LastBytesRead <= 0)
                    {
                        // nothing was read -> release the buffer.
                        byteBuffer.Release();
                        byteBuffer = null;
                        close = allocHandle.LastBytesRead < 0;
                        break;
                    }

                    Pipeline.FireChannelRead(byteBuffer);
                    allocHandle.IncMessagesRead(1);
                } while (allocHandle.ContinueReading());

                allocHandle.ReadComplete();
                _isReadPending = false;
                Pipeline.FireChannelReadComplete();
            }
            catch (Exception e) when (!e.IsFatal())
            {
                // Since this method returns void, all exceptions must be handled here.
                byteBuffer?.Release();
                allocHandle?.ReadComplete();
                _isReadPending = false;
                Pipeline.FireChannelReadComplete();
                Pipeline.FireExceptionCaught(e);
                close = true;
            }

            if (close)
            {
                if (Active)
                {
                    await HandleCloseAsync().ConfigureAwait(false);
                }
            }
        }

        protected override async void DoWrite(ChannelOutboundBuffer channelOutboundBuffer)
        {
            if (channelOutboundBuffer == null)
            {
                throw new ArgumentNullException(nameof(channelOutboundBuffer), "The channel outbound buffer cannot be null.");
            }

            try
            {
                _isWriteInProgress = true;
                while (true)
                {
                    object currentMessage = channelOutboundBuffer.Current;
                    if (currentMessage == null)
                    {
                        // Wrote all messages.
                        break;
                    }

                    var byteBuffer = currentMessage as IByteBuffer;
                    Fx.AssertAndThrow(byteBuffer != null, "channelOutBoundBuffer contents must be of type IByteBuffer");

                    if (byteBuffer.ReadableBytes == 0)
                    {
                        channelOutboundBuffer.Remove();
                        continue;
                    }

                    await _webSocket.SendAsync(byteBuffer.GetIoBuffer(), WebSocketMessageType.Binary, true, _writeCancellationTokenSource.Token).ConfigureAwait(false);
                    channelOutboundBuffer.Remove();
                }

                _isWriteInProgress = false;
            }
            catch (Exception e) when (!e.IsFatal())
            {
                // Since this method returns void, all exceptions must be handled here.

                _isWriteInProgress = false;
                Pipeline.FireExceptionCaught(e);
                await HandleCloseAsync().ConfigureAwait(false);
            }
        }

        private async Task<int> DoReadBytesAsync(IByteBuffer byteBuffer)
        {
            WebSocketReceiveResult receiveResult = await _webSocket
                .ReceiveAsync(new ArraySegment<byte>(byteBuffer.Array, byteBuffer.ArrayOffset + byteBuffer.WriterIndex, byteBuffer.WritableBytes), CancellationToken.None)
                .ConfigureAwait(false);
            if (receiveResult.MessageType == WebSocketMessageType.Text)
            {
                throw new ProtocolViolationException("Mqtt over WS message cannot be in text");
            }

            // Check if client closed WebSocket
            if (receiveResult.MessageType == WebSocketMessageType.Close)
            {
                return -1;
            }

            byteBuffer.SetWriterIndex(byteBuffer.WriterIndex + receiveResult.Count);
            return receiveResult.Count;
        }

        private void CancelPendingWrite()
        {
            try
            {
                _writeCancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // ignore this error
            }
        }

        private async Task HandleCloseAsync()
        {
            try
            {
                await CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                Abort();
            }
        }

        private void Abort()
        {
            _webSocket?.Abort();
        }
    }
}
