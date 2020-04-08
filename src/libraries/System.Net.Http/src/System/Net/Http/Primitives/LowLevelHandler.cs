// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Primitives
{
    // Temp, here to demonstrate API.
    public sealed class LowLevelHandler : HttpMessageHandler
    {
        protected internal override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Socket? socket = null;
            HttpConnection? connection = null;
            HttpRequestStream? stream = null;
            LowLevelStream? httpStream = null;

            try
            {
                socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new DnsEndPoint(request.RequestUri!.Host, request.RequestUri.Port)).ConfigureAwait(false);

                connection = new Http11Connection(new NetworkStream(socket, ownsSocket: true), ownsStream: true);

                // in HTTP/2 and HTTP/3 this might wait for an available stream. in HTTP/1 it be used for pipelining to wait for previous request to finish sending.
                stream = await connection.CreateNewRequestStreamAsync(cancellationToken).ConfigureAwait(false);

                httpStream = new LowLevelStream(connection, stream);
                HttpResponseMessage response = await httpStream.SendAsync(request, cancellationToken).ConfigureAwait(false);

                httpStream = null;
                stream = null;
                connection = null;
                socket = null;

                return response;
            }
            finally
            {
                if (httpStream != null) await httpStream.DisposeAsync().ConfigureAwait(false);
                if (stream != null) await stream.DisposeAsync().ConfigureAwait(false);
                if (connection != null) await connection.DisposeAsync().ConfigureAwait(false);
                socket?.Dispose();
            }
        }

        private static void WriteHeaders(HttpRequestStream stream, Headers.HttpHeaders headers)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in headers)
            {
                // final version will have overloads taking string as well as (IEnumerable<string> values, string separator).
                byte[] name = Encoding.ASCII.GetBytes(header.Key);
                byte[] value = Encoding.ASCII.GetBytes(string.Join(',', header.Value)); // i know ',' isn't technically correct for all headers. demo :).
                stream.WriteHeader(name, value);
            }
        }

        private static void AddHeaderToCollection(HttpHeaders headers, HttpHeaders? contentHeaders, HttpRequestStream stream, bool isFromTrailer)
        {
            if (!HeaderDescriptor.TryGet(stream.HeaderName.Span, out HeaderDescriptor descriptor))
            {
                // Invalid header name.
                throw new HttpRequestException(SR.Format(SR.net_http_invalid_response_header_name, Encoding.ASCII.GetString(stream.HeaderName.Span)));
            }

            if (isFromTrailer && descriptor.KnownHeader != null && (descriptor.KnownHeader.HeaderType & HttpHeaderType.NonTrailing) == HttpHeaderType.NonTrailing)
            {
                // Disallowed trailer fields.
                // A recipient MUST ignore fields that are forbidden to be sent in a trailer.
                return;
            }

            string headerValue = descriptor.GetHeaderValue(stream.HeaderValue.Span);

            // Note we ignore the return value from TryAddWithoutValidation. If the header can't be added, we silently drop it.
            // Request headers returned on the response must be treated as custom headers.
            if (isFromTrailer || (descriptor.HeaderType & HttpHeaderType.Content) != HttpHeaderType.Content)
            {
                headers.TryAddWithoutValidation((descriptor.HeaderType & HttpHeaderType.Request) == HttpHeaderType.Request ? descriptor.AsCustomHeader() : descriptor, headerValue);
            }
            else
            {
                contentHeaders!.TryAddWithoutValidation(descriptor, headerValue);
            }
        }

        private sealed class LowLevelStream : HttpBaseStream
        {
            private HttpConnection? _connection;
            private HttpRequestStream? _stream;
            private HttpResponseMessage? _response;

            public override bool CanRead => true;
            public override bool CanWrite => true;

            public LowLevelStream(HttpConnection connection, HttpRequestStream stream)
            {
                _connection = connection;
                _stream = stream;
                _response = new HttpResponseMessage();
            }

            public override async ValueTask DisposeAsync()
            {
                if (_stream != null)
                {
                    await _stream.DisposeAsync().ConfigureAwait(false);
                    _stream = null;
                }

                if (_connection != null)
                {
                    await _connection.DisposeAsync().ConfigureAwait(false);
                    _connection = null;
                }

                _response = null;

                await base.DisposeAsync().ConfigureAwait(false);
            }

            public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _stream!.ConfigureRequest(request.Content == null ? 0 : request.Content.Headers.ContentLength, hasTrailingHeaders: false);

                // final version can take HttpMethod, Version, and have overloads for string.
                // this writes the request line as well as "Host" or ":authority" header.
                byte[] origin = Encoding.ASCII.GetBytes($"{request.RequestUri!.IdnHost}:{request.RequestUri.Port}");
                byte[] method = Encoding.ASCII.GetBytes(request.Method.Method);
                byte[] uri = Encoding.ASCII.GetBytes(request.RequestUri.PathAndQuery);
                HttpVersion version = request.Version.Major == 1 && request.Version.Major == 0 ? HttpVersion.Version10 : HttpVersion.Version11;
                _stream.WriteRequest(origin, method, uri, version);

                WriteHeaders(_stream, request.Headers);

                if (request.Content == null)
                {
                    // Flush any remaining buffers.
                    await _stream.CompleteRequestAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    WriteHeaders(_stream, request.Content.Headers);

                    if (request.Headers.ExpectContinue != true)
                    {
                        await request.Content.CopyToAsync(this, cancellationToken).ConfigureAwait(false);

                        // Flush any remaining buffers.
                        await _stream.CompleteRequestAsync(cancellationToken).ConfigureAwait(false);

                        // Read until we hit a response. in HTTP/1 this will always be the first node. In HTTP/2 this might be e.g. an ALTSVC frame, so this would need more code to handle that.
                        // One can imagine a helper method to allow users to skip directly to an expected node.
                        while (await _stream.ReadAsync(cancellationToken).ConfigureAwait(false) != HttpReadType.Response) ;
                    }
                    else
                    {
                        // For Expect Continue, flush headers prior to sending content.
                        await _stream.FlushHeadersAsync(cancellationToken).ConfigureAwait(false);

                        bool contentSent = false;

                        while (true)
                        {
                            while (await _stream.ReadAsync(cancellationToken).ConfigureAwait(false) != HttpReadType.Response)
                            {
                            }

                            if ((int)_stream.StatusCode < 200)
                            {
                                // Skip headers for an informational response.
                                while (await _stream.ReadAsync(cancellationToken).ConfigureAwait(false) != HttpReadType.Headers) ;
                                while (await _stream.ReadNextHeaderAsync(cancellationToken).ConfigureAwait(false)) ;

                                if (_stream.StatusCode == HttpStatusCode.Continue && !contentSent)
                                {
                                    await request.Content.CopyToAsync(this, cancellationToken).ConfigureAwait(false);
                                    contentSent = true;
                                }
                            }
                            else
                            {
                                break;
                            }
                        }

                        if ((int)_stream.StatusCode < 300 && !contentSent)
                        {
                            // Sent Expect Continue header but never got a continue response. For non-error respronses, send content.
                            await request.Content.CopyToAsync(this, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                _response!.StatusCode = _stream.StatusCode;
                _response.Version = _stream.Version!;

                HttpConnectionResponseContent content = new HttpConnectionResponseContent();
                _response.Content = content;

                // Read response headers.
                while (await _stream.ReadAsync(cancellationToken).ConfigureAwait(false) != HttpReadType.Headers) ;
                while (await _stream.ReadNextHeaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    AddHeaderToCollection(_response.Headers, content.Headers, _stream, isFromTrailer: false);
                }

                if (content.Headers.ContentLength == 0)
                {
                    content.SetStream(EmptyReadStream.Instance);

                    while (true)
                    {
                        HttpReadType nodeType = await _stream.ReadAsync(cancellationToken).ConfigureAwait(false);
                        if (nodeType == HttpReadType.EndOfStream)
                        {
                            await DisposeAsync().ConfigureAwait(false);
                            break;
                        }
                        else if (nodeType == HttpReadType.Headers)
                        {
                            while (await _stream.ReadNextHeaderAsync(cancellationToken).ConfigureAwait(false))
                            {
                                AddHeaderToCollection(_response.TrailingHeaders, contentHeaders: null, _stream, isFromTrailer: true);
                            }
                        }
                    }
                }
                else
                {
                    content.SetStream(this);
                }

                HttpResponseMessage response = _response;
                _response = null;
                return response;
            }

            public override int Read(Span<byte> buffer)
            {
                throw new NotImplementedException();
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (_stream == null) return 0;

                int readLen = await _stream.ReadContentAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (readLen != 0) return readLen;

                while (true)
                {
                    HttpReadType nodeType = await _stream.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (nodeType == HttpReadType.EndOfStream)
                    {
                        await _stream.DisposeAsync().ConfigureAwait(false);
                        _stream = null;
                        _response = null;

                        return 0;
                    }
                    else if (nodeType == HttpReadType.Headers)
                    {
                        while (await _stream.ReadNextHeaderAsync(cancellationToken).ConfigureAwait(false))
                        {
                            AddHeaderToCollection(_response!.TrailingHeaders, contentHeaders: null, _stream, isFromTrailer: true);
                        }
                    }
                }
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                return _stream!.WriteContentAsync(buffer, cancellationToken);
            }
        }
    }
}
