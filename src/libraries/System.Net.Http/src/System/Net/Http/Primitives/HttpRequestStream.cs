// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Primitives
{
    /// <summary>
    /// More or less HttpRequestMessage/HttpResponseMessage/HttpContent rolled into one.
    /// </summary>
    public abstract class HttpRequestStream : IAsyncDisposable
    {
        // When Read() methods complete these will be filled in. Values are invalidated (memory re-used etc.) on next Read().
        public HttpReadType ReadType { get; protected set; }
        public Version? Version { get; protected set; }
        public HttpStatusCode StatusCode { get; protected set; }
        public ReadOnlyMemory<byte> HeaderName { get; protected set; }
        public ReadOnlyMemory<byte> HeaderValue { get; protected set; }
        public virtual ReadOnlyMemory<byte> AltSvc => ReadOnlyMemory<byte>.Empty; // only used for HTTP/2 and should only be used uncommonly; virtual to not allocate field.

        // In HTTP/1 this controls chunked encoding, in HTTP/3 this allows optimal DATA frame enveloping. it does not set any headers.
        public abstract void ConfigureRequest(long? contentLength, bool hasTrailingHeaders);

        // The non-async calls just fill a buffer that gets flushed upon FlushHeaders(), WriteContentAsync(), or CompleteRequestAsync().
        public abstract void WriteRequest(ReadOnlySpan<byte> origin, ReadOnlySpan<byte> method, ReadOnlySpan<byte> pathAndQuery, HttpVersion version);
        public abstract void WriteHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value);
        public abstract ValueTask FlushHeadersAsync(CancellationToken cancellationToken = default);
        public abstract ValueTask WriteContentAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
        public abstract ValueTask CompleteRequestAsync(CancellationToken cancellationToken = default);

        // Maximally complex flow:
        // - ReadAsync() until Headers
        // - ReadNextHeaderAsync() until false.
        // - ReadAsync() until Content (if available)
        // - ReadContentAsync() until false.
        // - ReadAsync() until Headers (trailing, if available)
        // - ReadNextHeaderAsync() until false.
        // - ReadAsync() until EndOfStream.
        public abstract ValueTask<HttpReadType> ReadAsync(CancellationToken cancellationToken = default);
        public abstract ValueTask<bool> ReadNextHeaderAsync(CancellationToken cancellationToken = default);
        public abstract ValueTask<int> ReadContentAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

        public abstract ValueTask DisposeAsync();

        // Convenience methods follow. TODO: optimize them.

        public virtual void WriteRequest(HttpMethod method, Uri uri, HttpVersion version)
        {
            string host = uri.IdnHost;
            int port = uri.Port;
            string origin =
                uri.HostNameType == UriHostNameType.IPv6
                ? $"[{host}]:{port}"
                : $"{host}:{port}";

            byte[] originBytes = Encoding.ASCII.GetBytes(origin);
            byte[] methodBytes = Encoding.ASCII.GetBytes(method.Method);
            byte[] uriBytes = Encoding.ASCII.GetBytes(uri.PathAndQuery);

            WriteRequest(originBytes, methodBytes, uriBytes, version);
        }

        public virtual void WriteHeader(string name, string value)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            byte[] valueBytes = Encoding.ASCII.GetBytes(value);

            WriteHeader(nameBytes, valueBytes);
        }

        public virtual void WriteHeader(string name, IEnumerable<string> values, string separator)
        {
            WriteHeader(name, string.Join(separator, values));
        }

        public async ValueTask ReadToResponseAsync(CancellationToken cancellationToken = default)
        {
            while (ReadType != HttpReadType.Response)
            {
                await ReadAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask<bool> ReadToHeadersAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                switch (ReadType)
                {
                    case HttpReadType.Headers:
                        return true;
                    case HttpReadType.Content:
                    case HttpReadType.TrailingHeaders:
                    case HttpReadType.EndOfStream:
                        return false;
                }
                await ReadAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask<bool> ReadToContentAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                switch (ReadType)
                {
                    case HttpReadType.Headers:
                        while (await ReadNextHeaderAsync(cancellationToken).ConfigureAwait(false)) ;
                        break;
                    case HttpReadType.Content:
                        return true;
                    case HttpReadType.TrailingHeaders:
                    case HttpReadType.EndOfStream:
                        return false;
                }
                await ReadAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask<bool> ReadToTrailingHeadersAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                switch (ReadType)
                {
                    case HttpReadType.Headers:
                        while (await ReadNextHeaderAsync(cancellationToken).ConfigureAwait(false)) ;
                        break;
                    case HttpReadType.Content:
                        // TODO: this could be made a bit more efficient by having a SkipContentAsync().
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
                        while (await ReadContentAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) ;
                        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                        break;
                    case HttpReadType.TrailingHeaders:
                        return true;
                    case HttpReadType.EndOfStream:
                        return false;
                }
                await ReadAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask ReadToEndOfStreamAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                switch (ReadType)
                {
                    case HttpReadType.Headers:
                        while (await ReadNextHeaderAsync(cancellationToken).ConfigureAwait(false)) ;
                        break;
                    case HttpReadType.Content:
                        // TODO: this could be made a bit more efficient by having a SkipContentAsync().
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
                        while (await ReadContentAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) ;
                        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                        break;
                    case HttpReadType.TrailingHeaders:
                        while (await ReadNextHeaderAsync(cancellationToken).ConfigureAwait(false)) ;
                        break;
                    case HttpReadType.EndOfStream:
                        return;
                }
                await ReadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
