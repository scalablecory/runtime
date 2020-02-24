// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Primitives
{
    internal sealed class Http11RequestStream : HttpRequestStream
    {
        private Http11Connection _connection;

        public Http11RequestStream(Http11Connection connection)
        {
            _connection = connection;
        }

        public override ValueTask DisposeAsync()
        {
            return default;
        }

        internal void SetCurrentReadType(HttpReadType readType)
        {
            ReadType = readType;
        }

        internal void SetCurrentResponseLine(Version version, HttpStatusCode statusCode)
        {
            Version = version;
            StatusCode = statusCode;
        }

        internal void SetCurrentHeader(ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> value)
        {
            HeaderName = name;
            HeaderValue = value;
        }

        public override void ConfigureRequest(long? contentLength, bool hasTrailingHeaders)
        {
            _connection.ConfigureRequest(contentLength, hasTrailingHeaders);
        }

        public override void WriteRequest(ReadOnlySpan<byte> origin, ReadOnlySpan<byte> method, ReadOnlySpan<byte> pathAndQuery, HttpVersion version)
        {
            _connection.WriteRequest(origin, method, pathAndQuery, version);
        }

        public override void WriteHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            _connection.WriteHeader(name, value);
        }

        public override ValueTask FlushHeadersAsync(CancellationToken cancellationToken)
        {
            return _connection.FlushHeadersAsync(cancellationToken);
        }

        public override ValueTask WriteContentAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return _connection.WriteContentAsync(buffer, cancellationToken);
        }

        public override ValueTask CompleteRequestAsync(CancellationToken cancellationToken = default)
        {
            return _connection.CompleteRequestAsync(cancellationToken);
        }

        public override ValueTask<HttpReadType> ReadAsync(CancellationToken cancellationToken = default)
        {
            return _connection.ReadAsync(this, cancellationToken);
        }

        public override ValueTask<bool> ReadNextHeaderAsync(CancellationToken cancellationToken = default)
        {
            return _connection.ReadNextHeaderAsync(this, cancellationToken);
        }

        public override ValueTask<int> ReadContentAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return _connection.ReadContentAsync(buffer, cancellationToken);
        }
    }
}
