// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Primitives
{
    public sealed class Http11Connection : HttpConnection
    {
        private static ushort s_encodedCrLf => BitConverter.IsLittleEndian ? (ushort)0x0A0D : (ushort)0x0D0A; // "\r\n"
        private static ushort s_encodedColonSpace => BitConverter.IsLittleEndian ? (ushort)0x203A : (ushort)0x3A20; // ": "
        private static ulong s_encodedNewLineHostPrefix => BitConverter.IsLittleEndian ? 0x203A74736F480A0DUL : 0x0D0A486F73743A20UL; // "\r\nHost: "
        private static readonly ReadOnlyMemory<byte> s_encodedCRLFMemory = new byte[] { (byte)'\r', (byte)'\n' };
        private static ReadOnlySpan<byte> s_EncodedTransferEncodingName => new byte[] { (byte)'t', (byte)'r', (byte)'a', (byte)'n', (byte)'s', (byte)'f', (byte)'e', (byte)'r', (byte)'-', (byte)'e', (byte)'n', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g' };
        private static ReadOnlySpan<byte> s_EncodedTransferEncodingChunkedValue => new byte[] { (byte)'c', (byte)'h', (byte)'u', (byte)'n', (byte)'k', (byte)'e', (byte)'d' };
        private static ReadOnlySpan<byte> s_EncodedContentLengthName => new byte[] { (byte)'c', (byte)'o', (byte)'n', (byte)'t', (byte)'e', (byte)'n', (byte)'t', (byte)'-', (byte)'l', (byte)'e', (byte)'n', (byte)'g', (byte)'t', (byte)'h' };

        private static readonly Func<Http11Connection, Http11RequestStream, CancellationToken, ValueTask<HttpReadType>> s_ReadResponse = (c, r, t) => c.ReadResponseAsync(r, t);
        private static readonly Func<Http11Connection, Http11RequestStream, CancellationToken, ValueTask<HttpReadType>> s_ReadToHeaders = (c, r, t) => { r.SetCurrentReadType(HttpReadType.Headers); return new ValueTask<HttpReadType>(HttpReadType.Headers); };
        private static readonly Func<Http11Connection, Http11RequestStream, CancellationToken, ValueTask<HttpReadType>> s_ReadToContent = (c, r, t) => c.ReadToContentAsync(r);
        private static readonly Func<Http11Connection, Http11RequestStream, CancellationToken, ValueTask<HttpReadType>> s_ReadToTrailingHeaders = (c, r, t) => { r.SetCurrentReadType(HttpReadType.TrailingHeaders); return new ValueTask<HttpReadType>(HttpReadType.TrailingHeaders); };
        private static readonly Func<Http11Connection, Http11RequestStream, CancellationToken, ValueTask<HttpReadType>> s_ReadToEndOfStream = (c, r, t) => { r.SetCurrentReadType(HttpReadType.EndOfStream); return new ValueTask<HttpReadType>(HttpReadType.EndOfStream); };

        internal Stream _stream;
        internal ArrayBuffer _readBuffer, _writeBuffer;
        private readonly List<ReadOnlyMemory<byte>> _gatheredWriteBuffer = new List<ReadOnlyMemory<byte>>(3);
        private bool _ownsStream, _requestIsChunked, _responseHasContentLength, _responseIsChunked, _readingFirstResponseChunk;
        private WriteState _writeState;
        private int _nextHeaderIndex;
        private ulong _responseContentBytesRemaining;
        private Func<Http11Connection, Http11RequestStream, CancellationToken, ValueTask<HttpReadType>> _readFunc;

        private enum WriteState : byte
        {
            Unstarted,
            RequestWritten,
            HeadersWritten,
            HeadersFlushed,
            ContentWritten,
            TrailingHeadersWritten,
            Finished
        }

        public Http11Connection(Stream stream, bool ownsStream = true)
        {
            _ownsStream = ownsStream;
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _readBuffer = new ArrayBuffer(initialSize: 4096, usePool: true);
            _writeBuffer = new ArrayBuffer(initialSize: 4096, usePool: true);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_ownsStream) await _stream.DisposeAsync().ConfigureAwait(false);
            _readBuffer.Dispose();
            _writeBuffer.Dispose();
        }

        public override ValueTask<HttpRequestStream> CreateNewRequestStreamAsync(CancellationToken cancellationToken = default)
        {
            Debug.Assert(_writeBuffer.ActiveLength == 0);
            Debug.Assert(_nextHeaderIndex == 0);
            Debug.Assert(_responseContentBytesRemaining == 0);
            _writeState = WriteState.Unstarted;
            _requestIsChunked = true;
            _responseHasContentLength = false;
            _responseIsChunked = false;
            _readingFirstResponseChunk = false;
            _readFunc = s_ReadResponse;
            return new ValueTask<HttpRequestStream>(new Http11RequestStream(this));
        }

        internal void ConfigureRequest(long? contentLength, bool hasTrailingHeaders)
        {
            _requestIsChunked = contentLength == null || hasTrailingHeaders;
        }

        internal void WriteRequest(ReadOnlySpan<byte> origin, ReadOnlySpan<byte> method, ReadOnlySpan<byte> pathAndQuery, HttpVersion version)
        {
            if (_writeState != WriteState.Unstarted)
            {
                throw new InvalidOperationException();
            }

            if (origin.Length == 0 || method.Length == 0 || pathAndQuery.Length == 0 || version._encoded == 0)
            {
                throw new ArgumentException();
            }

            int len = GetEncodeRequestLength(origin, method, pathAndQuery, version);
            _writeBuffer.EnsureAvailableSpace(len);
            EncodeRequest(origin, method, pathAndQuery, version, _writeBuffer.AvailableSpan);
            _writeBuffer.Commit(len);
            _writeState = WriteState.RequestWritten;
        }

        internal static int GetEncodeRequestLength(ReadOnlySpan<byte> origin, ReadOnlySpan<byte> method, ReadOnlySpan<byte> uri, HttpVersion version)
        {
            return origin.Length + method.Length + uri.Length + 20; // {method} {uri} HTTP/x.x\r\nHost: {origin}\r\n
        }

        internal static unsafe void EncodeRequest(ReadOnlySpan<byte> origin, ReadOnlySpan<byte> method, ReadOnlySpan<byte> uri, HttpVersion version, Span<byte> buffer)
        {
            Debug.Assert(origin.Length > 0);
            Debug.Assert(method.Length > 0);
            Debug.Assert(uri.Length > 0);
            Debug.Assert(buffer.Length >= GetEncodeRequestLength(origin, method, uri, version));

            ref byte pBuf = ref MemoryMarshal.GetReference(buffer);

            Unsafe.CopyBlockUnaligned(ref pBuf, ref MemoryMarshal.GetReference(method), (uint)method.Length);
            pBuf = ref Unsafe.Add(ref pBuf, (IntPtr)(uint)method.Length);

            pBuf = (byte)' ';
            pBuf = ref Unsafe.Add(ref pBuf, 1);

            Unsafe.CopyBlockUnaligned(ref pBuf, ref MemoryMarshal.GetReference(uri), (uint)uri.Length);
            pBuf = ref Unsafe.Add(ref pBuf, (IntPtr)(uint)uri.Length);

            pBuf = (byte)' ';
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pBuf, 1), version._encoded); // "HTTP/x.x"
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pBuf, 9), s_encodedNewLineHostPrefix); // "\r\nHost: "
            pBuf = ref Unsafe.Add(ref pBuf, 17);

            Unsafe.CopyBlockUnaligned(ref pBuf, ref MemoryMarshal.GetReference(origin), (uint)origin.Length);
            pBuf = ref Unsafe.Add(ref pBuf, (IntPtr)(uint)origin.Length);

            Unsafe.WriteUnaligned(ref pBuf, s_encodedCrLf);
            pBuf = ref Unsafe.Add(ref pBuf, 2);

            int length = (int)(void*)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(buffer), ref pBuf);
            Debug.Assert(length == GetEncodeRequestLength(origin, method, uri, version));
        }

        internal void WriteHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            if (name.Length == 0)
            {
                throw new ArgumentException();
            }

            switch (_writeState)
            {
                case WriteState.Unstarted:
                    throw new InvalidOperationException($"Must call {nameof(HttpRequestStream)}.{nameof(HttpRequestStream.WriteRequest)} prior to writing headers.");
                case WriteState.RequestWritten:
                    _writeState = WriteState.HeadersWritten;
                    break;
                case WriteState.HeadersWritten:
                case WriteState.TrailingHeadersWritten:
                    break;
                case WriteState.HeadersFlushed:
                case WriteState.ContentWritten:
                    // The first trailing header is being written. End chunked content with a 0-length chunk.
                    if (!_requestIsChunked) throw new InvalidOperationException("Can not write headers after content without trailing headers being enabled.");
                    WriteChunkEnvelopeStart(0);
                    _writeState = WriteState.TrailingHeadersWritten;
                    break;
                default: // case WriteState.Finished:
                    Debug.Assert(_writeState == WriteState.Finished);
                    throw new InvalidOperationException("Can not write headers after the request has been completed.");
            }

            int len = GetEncodeHeaderLength(name, value);
            _writeBuffer.EnsureAvailableSpace(len);
            EncodeHeader(name, value, _writeBuffer.AvailableSpan);
            _writeBuffer.Commit(len);
        }

        internal static int GetEncodeHeaderLength(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            Debug.Assert(name.Length > 0);
            return name.Length + value.Length + 4; // {name}: {value}\r\n
        }

        internal static unsafe void EncodeHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value, Span<byte> buffer)
        {
            Debug.Assert(name.Length > 0);
            Debug.Assert(buffer.Length >= GetEncodeHeaderLength(name, value));

            ref byte pBuf = ref MemoryMarshal.GetReference(buffer);

            Unsafe.CopyBlockUnaligned(ref pBuf, ref MemoryMarshal.GetReference(name), (uint)name.Length);
            pBuf = ref Unsafe.Add(ref pBuf, (IntPtr)(uint)name.Length);

            Unsafe.WriteUnaligned(ref pBuf, s_encodedColonSpace);
            pBuf = ref Unsafe.Add(ref pBuf, 2);

            if (value.Length != 0)
            {
                Unsafe.CopyBlockUnaligned(ref pBuf, ref MemoryMarshal.GetReference(value), (uint)value.Length);
                pBuf = ref Unsafe.Add(ref pBuf, (IntPtr)(uint)value.Length);
            }

            Unsafe.WriteUnaligned(ref pBuf, s_encodedCrLf);
            pBuf = ref Unsafe.Add(ref pBuf, 2);

            int length = (int)(void*)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(buffer), ref pBuf);
            Debug.Assert(length == GetEncodeHeaderLength(name, value));
        }

        private void WriteCRLF()
        {
            _writeBuffer.EnsureAvailableSpace(2);

            ref byte pBuf = ref MemoryMarshal.GetReference(_writeBuffer.AvailableSpan);
            Unsafe.WriteUnaligned(ref pBuf, s_encodedCrLf);

            _writeBuffer.Commit(2);
        }

        private void FlushHeadersHelper(bool completingRequest)
        {
            switch (_writeState)
            {
                case WriteState.Unstarted:
                    throw new InvalidOperationException($"{nameof(HttpRequestStream)}.{nameof(HttpRequestStream.WriteRequest)} must be called prior to calling this.");
                case WriteState.RequestWritten:
                case WriteState.HeadersWritten:
                    if (!completingRequest)
                    {
                        _writeState = WriteState.HeadersFlushed;
                    }
                    else
                    {
                        _writeState = WriteState.Finished;
                        if (_requestIsChunked)
                        {
                            // Need to end headers and trailing headers in this call.
                            WriteCRLF();
                        }
                    }
                    break;
                case WriteState.TrailingHeadersWritten:
                    _writeState = WriteState.Finished;
                    break;
                case WriteState.HeadersFlushed:
                case WriteState.ContentWritten:
                    if (!_requestIsChunked)
                    {
                        if (completingRequest)
                        {
                            Debug.Assert(_writeBuffer.ActiveLength == 0);
                            _writeState = WriteState.Finished;
                            return;
                        }

                        throw new InvalidOperationException("Can not write headers after content without trailing headers being enabled.");
                    }

                    // 0-length chunk to end content is normally written with the first trailing
                    // header written, otherwise it needs to be written here.
                    WriteChunkEnvelopeStart(0);
                    _writeState = WriteState.Finished;
                    break;
                default: //case WriteState.Finished:
                    Debug.Assert(_writeState == WriteState.Finished);

                    if (completingRequest)
                    {
                        Debug.Assert(_writeBuffer.ActiveLength == 0);
                        return;
                    }

                    throw new InvalidOperationException("Cannot flush headers on an already completed request.");
            }

            WriteCRLF(); // CRLF to end headers.
        }

        internal async ValueTask FlushHeadersAsync(CancellationToken cancellationToken)
        {
            FlushHeadersHelper(completingRequest: false);
            await _stream.WriteAsync(_writeBuffer.ActiveMemory, cancellationToken).ConfigureAwait(false);
            _writeBuffer.Discard(_writeBuffer.ActiveLength);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async ValueTask CompleteRequestAsync(CancellationToken cancellationToken)
        {
            FlushHeadersHelper(completingRequest: true);
            if (_writeBuffer.ActiveLength != 0)
            {
                await _stream.WriteAsync(_writeBuffer.ActiveMemory, cancellationToken).ConfigureAwait(false);
                _writeBuffer.Discard(_writeBuffer.ActiveLength);
            }
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        internal ValueTask WriteContentAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            switch (_writeState)
            {
                case WriteState.RequestWritten:
                case WriteState.HeadersWritten:
                    return WriteHeadersAndContentAsync(buffer, cancellationToken);
                case WriteState.HeadersFlushed:
                case WriteState.ContentWritten:
                    _writeState = WriteState.ContentWritten;
                    if (!_requestIsChunked) return _stream.WriteAsync(buffer, cancellationToken);
                    else return WriteChunkedContentAsync(buffer, cancellationToken);
                default:
                    Debug.Assert(_writeState == WriteState.TrailingHeadersWritten || _writeState == WriteState.Finished);
                    return new ValueTask(Task.FromException(ExceptionDispatchInfo.SetCurrentStackTrace(new InvalidOperationException("Content can not be written after the request has been completed or trailing headers have been written."))));
            }
        }

        private ValueTask WriteChunkedContentAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            WriteChunkEnvelopeStart(buffer.Length);

            _gatheredWriteBuffer.Clear();
            _gatheredWriteBuffer.Add(_writeBuffer.ActiveMemory);
            _gatheredWriteBuffer.Add(buffer);
            _gatheredWriteBuffer.Add(s_encodedCRLFMemory);

            // It looks weird to discard the buffer before sending has completed, but it works fine.
            // This is done to elide an async state machine allocation that would otherwise be needed to await the write and then discard.
            _writeBuffer.Discard(_writeBuffer.ActiveLength);

            return _stream.WriteAsync(_gatheredWriteBuffer, cancellationToken);
        }

        private void WriteChunkEnvelopeStart(int chunkLength)
        {
            Debug.Assert(chunkLength >= 0);

            _writeBuffer.EnsureAvailableSpace(32);

            Span<byte> buffer = _writeBuffer.AvailableSpan;

            bool success = Utf8Formatter.TryFormat((uint)chunkLength, buffer, out int envelopeLength, format: 'X');
            Debug.Assert(success == true);

            Debug.Assert(envelopeLength < 30);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(buffer), envelopeLength), s_encodedCrLf);

            _writeBuffer.Commit(envelopeLength + 2);
        }

        private ValueTask WriteHeadersAndContentAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            FlushHeadersHelper(completingRequest: false);

            if (!_requestIsChunked)
            {
                _gatheredWriteBuffer.Clear();
                _gatheredWriteBuffer.Add(_writeBuffer.ActiveMemory);
                _gatheredWriteBuffer.Add(buffer);

                // It looks weird to discard the buffer before sending has completed, but it works fine.
                // This is done to elide an async state machine allocation that would otherwise be needed to await the write and then discard.
                _writeBuffer.Discard(_writeBuffer.ActiveLength);

                return _stream.WriteAsync(_gatheredWriteBuffer, cancellationToken);
            }
            else
            {
                return WriteChunkedContentAsync(buffer, cancellationToken);
            }
        }

        internal ValueTask<HttpReadType> ReadAsync(Http11RequestStream requestStream, CancellationToken cancellationToken)
        {
            return _readFunc(this, requestStream, cancellationToken);
        }

        private async ValueTask<HttpReadType> ReadResponseAsync(Http11RequestStream requestStream, CancellationToken cancellationToken)
        {
            while (true)
            {
                HttpReadType? nodeType = TryReadResponse(requestStream);
                if (nodeType != null)
                {
                    requestStream.SetCurrentReadType(nodeType.GetValueOrDefault());
                    return nodeType.GetValueOrDefault();
                }

                _readBuffer.EnsureAvailableSpace(1);

                int readBytes = await _stream.ReadAsync(_readBuffer.AvailableMemory, cancellationToken).ConfigureAwait(false);
                if (readBytes == 0) throw new Exception("Unexpected EOF");

                _readBuffer.Commit(readBytes);
            }
        }

        private HttpReadType? TryReadResponse(Http11RequestStream requestStream)
        {
            if (_readBuffer.ActiveLength == 0)
            {
                return null;
            }

            Version version;
            System.Net.HttpStatusCode statusCode;

            ReadOnlySpan<byte> buffer = _readBuffer.ActiveSpan;

            if (buffer.Length >= 9 && buffer[8] == ' ')
            {
                ulong x = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(buffer));

                if (x == HttpVersion.Version11._encoded)
                {
                    version = System.Net.HttpVersion.Version11;
                }
                else if (x == HttpVersion.Version10._encoded)
                {
                    version = System.Net.HttpVersion.Version10;
                }
                else
                {
                    goto unknownVersion;
                }

                buffer = buffer.Slice(9);
                goto parseStatusCode;
            }
            else
            {
                goto unknownVersion;
            }

        parseStatusCode:
            if (buffer.Length < 4)
            {
                return null;
            }

            uint s100, s10, s1;
            if ((s100 = buffer[0] - (uint)'0') > 9 || (s10 = buffer[1] - (uint)'0') > 9 || (s1 = buffer[2] - (uint)'0') > 9 || buffer[3] != (byte)' ')
            {
                throw new Exception("invalid status code.");
            }

            statusCode = (System.Net.HttpStatusCode)(s100 * 100u + s10 * 10u + s1);
            buffer = buffer.Slice(4);

            int idx = buffer.IndexOf((byte)'\n');
            if (idx == -1)
            {
                return null;
            }

            buffer = buffer.Slice(idx + 1);
            _readBuffer.Discard(_readBuffer.ActiveLength - buffer.Length);

            requestStream.SetCurrentResponseLine(version, statusCode);

            // reset values from previous requests.
            _responseContentBytesRemaining = 0;
            _responseHasContentLength = false;
            _responseIsChunked = false;
            _readingFirstResponseChunk = true;

            _readFunc = s_ReadToHeaders;
            return HttpReadType.Response;

        unknownVersion:
            idx = buffer.IndexOf((byte)' ');
            if (idx == -1) return null;
            version = System.Net.HttpVersion.Unknown;
            buffer = buffer.Slice(idx + 1);
            goto parseStatusCode;
        }

        internal async ValueTask<bool> ReadNextHeaderAsync(Http11RequestStream requestStream, CancellationToken cancellationToken)
        {
            if (_readFunc != s_ReadToHeaders && _readFunc != s_ReadToTrailingHeaders)
            {
                return false;
            }

            bool eof = false;

            while (true)
            {
                bool? res = TryReadNextHeader(requestStream);
                if (res != null) return res.GetValueOrDefault();

                if (eof)
                {
                    throw new Exception("Unexpected EOF.");
                }

                _readBuffer.Grow();

                int readBytes = await _stream.ReadAsync(_readBuffer.AvailableMemory, cancellationToken).ConfigureAwait(false);

                if (readBytes != 0)
                {
                    _readBuffer.Commit(readBytes);
                }
                else
                {
                    eof = true;
                }
            }
        }

        private unsafe bool? TryReadNextHeader(Http11RequestStream requestStream)
        {
            if (_nextHeaderIndex != 0)
            {
                _readBuffer.Discard(_nextHeaderIndex);
                _nextHeaderIndex = 0;
            }

            Span<byte> originalBuffer = _readBuffer.ActiveSpan;
            Span<byte> buffer = originalBuffer;

            if (buffer.Length == 0)
            {
                return null;
            }

            int crlfSize = 1;
            switch (buffer[0])
            {
                case (byte)'\r':
                    if (buffer.Length < 2) return null;
                    if (buffer[1] != '\n') throw new Exception("Invalid newline, expected CRLF");
                    ++crlfSize;
                    goto case (byte)'\n';
                case (byte)'\n':
                    _readBuffer.Discard(crlfSize);
                    _readFunc =
                        _readingFirstResponseChunk == false ? s_ReadToEndOfStream : // Last header of chunked encoding.
                        (int)requestStream.StatusCode < 200 ? s_ReadResponse : // Informational status code. more responses coming.
                        s_ReadToContent; // Move to content.
                    return false;
            }

            int idx = buffer.IndexOf((byte)':');
            if (idx == -1) return null;

            ReadOnlyMemory<byte> headerName = _readBuffer.ActiveMemory.Slice(0, idx);

            // Skip OWS.
            while (true)
            {
                if (++idx == buffer.Length) return null;

                byte ch = buffer[idx];
                if (ch != ' ' && ch != '\t')
                {
                    break;
                }
            }

            int valueStartIdx = idx;

            while (true)
            {
                buffer = buffer.Slice(idx);

                idx = buffer.IndexOf((byte)'\n');
                if (idx == -1 || (idx + 1) == buffer.Length) return null;

                // Check if header continues on the next line.
                if (buffer[idx + 1] == '\t')
                {
                    // Replace CRLF with SPSP and loop.
                    buffer[idx] = (byte)' ';
                    buffer[idx - 1] = (byte)' ';
                    ++idx;
                    continue;
                }

                int bufferStartIdx = (int)(void*)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(originalBuffer), ref MemoryMarshal.GetReference(buffer));
                int valueEndIdx = bufferStartIdx + (buffer[idx - 1] == '\r' ? idx - 1 : idx);
                _nextHeaderIndex = bufferStartIdx + idx + 1;

                ReadOnlyMemory<byte> headerValue = _readBuffer.ActiveMemory.Slice(valueStartIdx, valueEndIdx - valueStartIdx);
                requestStream.SetCurrentHeader(headerName, headerValue);

                if (HeaderNamesEqual(headerName.Span, s_EncodedContentLengthName))
                {
                    if (!TryParseDecimalInteger(headerValue.Span, out _responseContentBytesRemaining))
                    {
                        throw new Exception("Response Content-Length header contains a malformed or too-large value.");
                    }
                    _responseHasContentLength = true;
                }
                else if (HeaderNamesEqual(headerName.Span, s_EncodedTransferEncodingName))
                {
                    if (headerValue.Span.SequenceEqual(s_EncodedTransferEncodingChunkedValue))
                    {
                        _responseIsChunked = true;
                    }
                }

                return true;
            }
        }

        private static bool HeaderNamesEqual(ReadOnlySpan<byte> wireValue, ReadOnlySpan<byte> expectedValueLowerCase)
        {
            if (wireValue.Length != expectedValueLowerCase.Length) return false;

            ref byte xRef = ref MemoryMarshal.GetReference(wireValue);
            ref byte yRef = ref MemoryMarshal.GetReference(expectedValueLowerCase);

            for (uint i = 0; i < (uint)wireValue.Length; ++i)
            {
                byte xv = Unsafe.Add(ref xRef, (IntPtr)i);

                if ((xv - (uint)'A') <= ('Z' - 'A'))
                {
                    xv |= 0x20;
                }

                if (xv != Unsafe.Add(ref yRef, (IntPtr)i)) return false;
            }

            return true;
        }

        private ValueTask<HttpReadType> ReadToContentAsync(Http11RequestStream requestStream)
        {
            HttpReadType readType =
                _responseIsChunked || !_responseHasContentLength || _responseContentBytesRemaining != 0
                ? HttpReadType.Content :
                HttpReadType.EndOfStream;

            requestStream.SetCurrentReadType(readType);
            return new ValueTask<HttpReadType>(readType);
        }

        internal ValueTask<int> ReadContentAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_readFunc != s_ReadToContent)
            {
                return new ValueTask<int>(0);
            }
            else if (!_responseIsChunked || _responseContentBytesRemaining != 0)
            {
                // Unchunked, or chunked and mid-chunk.
                return ReadUnenvelopedContentAsync(buffer, cancellationToken);
            }
            else
            {
                // Chunked encoding, either start of content or have reached end of previous chunk.
                return ReadChunkedContentStartAsync(buffer, cancellationToken);
            }
        }

        private async ValueTask<int> ReadChunkedContentStartAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            ulong? chunkSize;
            while (true)
            {
                chunkSize = TryReadNextChunkSize();
                if (chunkSize != null) break;

                _readBuffer.EnsureAvailableSpace(1);

                int readBytes = await _stream.ReadAsync(_readBuffer.AvailableMemory, cancellationToken).ConfigureAwait(false);
                if (readBytes == 0) throw new Exception("Unexpected EOF, expected chunk envelope.");

                _readBuffer.Commit(readBytes);
            }

            if (chunkSize.GetValueOrDefault() == 0)
            {
                // Final chunk. Move to trailing headers.
                _readFunc = s_ReadToTrailingHeaders;
                return 0;
            }

            _responseContentBytesRemaining = chunkSize.GetValueOrDefault();
            return await ReadUnenvelopedContentAsync(buffer, cancellationToken);
        }

        private async ValueTask<int> ReadUnenvelopedContentAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            int maxReadLength = (int)Math.Min((uint)buffer.Length, _responseContentBytesRemaining);

            if (maxReadLength == 0)
            {
                // End of content reached, or empty buffer.
                _readFunc = s_ReadToEndOfStream;
                return 0;
            }

            int bufferedLength = _readBuffer.ActiveLength;
            if (bufferedLength != 0)
            {
                int takeLength = Math.Min(bufferedLength, maxReadLength);
                _readBuffer.ActiveSpan.Slice(0, takeLength).CopyTo(buffer.Span);
                _readBuffer.Discard(takeLength);

                _responseContentBytesRemaining -= (uint)takeLength;
                return takeLength;
            }
            else
            {
                int readBytes = await _stream.ReadAsync(buffer.Slice(0, maxReadLength), cancellationToken).ConfigureAwait(false);
                if (readBytes == 0) throw new Exception("Unexpected EOF");

                _responseContentBytesRemaining -= (uint)readBytes;
                return readBytes;
            }
        }

        private ulong? TryReadNextChunkSize()
        {
            Span<byte> buffer = _readBuffer.ActiveSpan;
            int previousChunkNewlineLength = 0;

            if (!_readingFirstResponseChunk)
            {
                if (buffer.Length == 0)
                {
                    return null;
                }

                byte b = buffer[0];
                if (b == '\r')
                {
                    if (buffer.Length == 1)
                    {
                        return null;
                    }

                    if (buffer[1] == '\n')
                    {
                        buffer = buffer.Slice(2);
                        previousChunkNewlineLength = 2;
                    }
                    else
                    {
                        throw new Exception("Invalid chunked envelope; expected newline to end chunk.");
                    }
                }
                else if (b == '\n')
                {
                    buffer = buffer.Slice(1);
                    previousChunkNewlineLength = 1;
                }
                else
                {
                    throw new Exception("Invalid chunked envelope; expected newline to end chunk.");
                }
            }

            int lfIdx = buffer.IndexOf((byte)'\n');
            int valueEndIdx = lfIdx;

            if (lfIdx == -1)
            {
                return null;
            }

            if (lfIdx != 0 && buffer[lfIdx - 1] == '\r')
            {
                valueEndIdx = lfIdx - 1;
            }

            if (valueEndIdx != 0)
            {
                int chunkExtBeginIdx = buffer.Slice(0, valueEndIdx).IndexOf((byte)';');
                if (chunkExtBeginIdx != -1) valueEndIdx = chunkExtBeginIdx;
            }

            if (valueEndIdx == 0 || !TryParseHexInteger(buffer.Slice(0, valueEndIdx), out ulong chunkSize))
            {
                throw new Exception($"Invalid chunked envelope; expected chunk size. idx: {valueEndIdx}.");
            }

            _readBuffer.Discard(lfIdx + 1 + previousChunkNewlineLength);
            _readingFirstResponseChunk = false;

            return chunkSize;
        }

        private static bool TryParseHexInteger(ReadOnlySpan<byte> buffer, out ulong value)
        {
            if (buffer.Length == 0)
            {
                value = default;
                return false;
            }

            ulong contentLength = 0;
            foreach (byte b in buffer)
            {
                uint digit;

                if ((digit = b - (uint)'0') <= 9)
                {
                }
                else if ((digit = b - (uint)'a') <= 'f' - 'a')
                {
                    digit += 10;
                }
                else if ((digit = b - (uint)'A') <= 'F' - 'A')
                {
                    digit += 10;
                }
                else
                {
                    value = default;
                    return false;
                }

                try
                {
                    contentLength = checked(contentLength * 16u + digit);
                }
                catch (OverflowException)
                {
                    value = default;
                    return false;
                }
            }

            value = contentLength;
            return true;
        }

        private static bool TryParseDecimalInteger(ReadOnlySpan<byte> buffer, out ulong value)
        {
            if (buffer.Length == 0)
            {
                value = default;
                return false;
            }

            ulong contentLength = 0;
            foreach (byte b in buffer)
            {
                uint digit = b - (uint)'0';
                if (digit > 9)
                {
                    value = default;
                    return false;
                }

                try
                {
                    contentLength = checked(contentLength * 10u + digit);
                }
                catch (OverflowException)
                {
                    value = default;
                    return false;
                }
            }

            value = contentLength;
            return true;
        }
    }
}
