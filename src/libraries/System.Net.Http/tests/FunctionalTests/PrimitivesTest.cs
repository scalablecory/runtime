using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http.Primitives;
using System.Net.Sockets;
using System.Net.Test.Common;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.Http.Functional.Tests
{
    public class PrimitivesTest
    {
        [Fact]
        public async Task Get_NoContent_Success()
        {
            await RunClientAndServer(
                async uri =>
                {
                    await using HttpConnection connection = await OpenConnection(uri);
                    await using HttpRequestStream stream = await connection.CreateNewRequestStreamAsync();

                    stream.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                    stream.WriteRequest(HttpMethod.Get, uri, Primitives.HttpVersion.Version11);
                    await stream.CompleteRequestAsync();

                    HttpResponseData response = await ReadResponse(stream);
                    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                },
                async server =>
                {
                    HttpRequestData data = await server.AcceptConnectionSendResponseAndCloseAsync(HttpStatusCode.NoContent);

                    Assert.Equal("GET", data.Method);
                    Assert.Equal(server.Address.PathAndQuery, data.Path);
                    Assert.Equal(GetOriginFromUri(server.Address), data.GetSingleHeaderValue("Host"));
                });
        }

        [Fact]
        public async Task Get_MultipleRequestsWithNoContent_Success()
        {
            await RunClientAndServer(
                async uri =>
                {
                    await using HttpConnection connection = await OpenConnection(uri);

                    await using (HttpRequestStream stream = await connection.CreateNewRequestStreamAsync())
                    {
                        stream.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                        stream.WriteRequest(HttpMethod.Get, uri, Primitives.HttpVersion.Version11);
                        stream.WriteHeader("Request-A", "Request-A");
                        await stream.CompleteRequestAsync();

                        HttpResponseData response = await ReadResponse(stream);
                        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                    }

                    await using (HttpRequestStream stream = await connection.CreateNewRequestStreamAsync())
                    {
                        stream.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                        stream.WriteRequest(HttpMethod.Get, uri, Primitives.HttpVersion.Version11);
                        stream.WriteHeader("Request-B", "Request-B");
                        await stream.CompleteRequestAsync();

                        HttpResponseData response = await ReadResponse(stream);
                        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                    }
                },
                async server =>
                {
                    using GenericLoopbackConnection connection = await server.EstablishGenericConnectionAsync();

                    HttpRequestData data = await connection.ReadRequestDataAsync();
                    Assert.Equal("GET", data.Method);
                    Assert.Equal(server.Address.PathAndQuery, data.Path);
                    Assert.Equal(GetOriginFromUri(server.Address), data.GetSingleHeaderValue("Host"));
                    Assert.Equal("Request-A", data.GetSingleHeaderValue("Request-A"));
                    await connection.SendResponseAsync(HttpStatusCode.NoContent);

                    data = await connection.ReadRequestDataAsync();
                    Assert.Equal("GET", data.Method);
                    Assert.Equal(server.Address.PathAndQuery, data.Path);
                    Assert.Equal(GetOriginFromUri(server.Address), data.GetSingleHeaderValue("Host"));
                    Assert.Equal("Request-B", data.GetSingleHeaderValue("Request-B"));
                    await connection.SendResponseAsync(HttpStatusCode.NoContent);
                });
        }

        [Fact]
        public async Task Get_WithResponseContent_Success()
        {
            string content = "Hello, World!";

            await RunClientAndServer(
                async uri =>
                {
                    await using HttpConnection connection = await OpenConnection(uri);
                    await using HttpRequestStream stream = await connection.CreateNewRequestStreamAsync();

                    stream.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                    stream.WriteRequest(HttpMethod.Get, uri, Primitives.HttpVersion.Version11);
                    await stream.CompleteRequestAsync();

                    HttpResponseData response = await ReadResponse(stream);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(content.Length.ToString(CultureInfo.InvariantCulture), response.GetSingleHeaderValue("Content-Length"));
                    Assert.Equal(content, Encoding.ASCII.GetString(response.Body));
                },
                async server =>
                {
                    HttpRequestData data = await server.AcceptConnectionSendResponseAndCloseAsync(content: content);

                    Assert.Equal("GET", data.Method);
                    Assert.Equal(server.Address.PathAndQuery, data.Path);
                    Assert.Equal(GetOriginFromUri(server.Address), data.GetSingleHeaderValue("Host"));
                });
        }

        [Theory]
        [InlineData("D\r\nHello, World!\r\n0\r\n\r\n")]
        [InlineData("6\r\nHello,\r\n1\r\n \r\n6\r\nWorld!\r\n0\r\n\r\n")]
        public async Task Get_WithChunkedResponseContent_Success(string chunkedResponseContent)
        {
            await RunClientAndServer(
                async uri =>
                {
                    await using HttpConnection connection = await OpenConnection(uri);
                    await using HttpRequestStream stream = await connection.CreateNewRequestStreamAsync();

                    stream.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                    stream.WriteRequest(HttpMethod.Get, uri, Primitives.HttpVersion.Version11);
                    await stream.CompleteRequestAsync();

                    HttpResponseData response = await ReadResponse(stream);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal("Hello, World!", Encoding.ASCII.GetString(response.Body));
                },
                async server =>
                {
                    HttpRequestData data = await server.AcceptConnectionSendResponseAndCloseAsync(content: chunkedResponseContent, additionalHeaders: new[]
                    {
                        new HttpHeaderData("Content-Length", null),
                        new HttpHeaderData("Transfer-Encoding", "chunked")
                    });

                    Assert.Equal("GET", data.Method);
                    Assert.Equal(server.Address.PathAndQuery, data.Path);
                    Assert.Equal(GetOriginFromUri(server.Address), data.GetSingleHeaderValue("Host"));
                });
        }

        [Theory]
        [InlineData("D\r\nHello, World!\r\n0\r\n\r\n")]
        [InlineData("6\r\nHello,\r\n1\r\n \r\n6\r\nWorld!\r\n0\r\n\r\n")]
        public async Task Get_MultipleRequestsWithChunkedResponseContent_Success(string chunkedResponseContent)
        {
            await RunClientAndServer(
                async uri =>
                {
                    await using HttpConnection connection = await OpenConnection(uri);

                    await using (HttpRequestStream stream = await connection.CreateNewRequestStreamAsync())
                    {
                        stream.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                        stream.WriteRequest(HttpMethod.Get, uri, Primitives.HttpVersion.Version11);
                        stream.WriteHeader("Request-A", "Request-A");
                        await stream.CompleteRequestAsync();

                        HttpResponseData response = await ReadResponse(stream);
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        Assert.Equal("Response-A", response.GetSingleHeaderValue("Response-A"));
                        Assert.Equal("Hello, World!", Encoding.ASCII.GetString(response.Body));
                    }

                    await using (HttpRequestStream stream = await connection.CreateNewRequestStreamAsync())
                    {
                        stream.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                        stream.WriteRequest(HttpMethod.Get, uri, Primitives.HttpVersion.Version11);
                        stream.WriteHeader("Request-B", "Request-B");
                        await stream.CompleteRequestAsync();

                        HttpResponseData response = await ReadResponse(stream);
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        Assert.Equal("Response-B", response.GetSingleHeaderValue("Response-B"));
                        Assert.Equal("Hello, World!", Encoding.ASCII.GetString(response.Body));
                    }

                },
                async server =>
                {
                    using GenericLoopbackConnection connection = await server.EstablishGenericConnectionAsync();

                    HttpRequestData data = await connection.ReadRequestDataAsync();
                    Assert.Equal("GET", data.Method);
                    Assert.Equal(server.Address.PathAndQuery, data.Path);
                    Assert.Equal(GetOriginFromUri(server.Address), data.GetSingleHeaderValue("Host"));
                    Assert.Equal("Request-A", data.GetSingleHeaderValue("Request-A"));
                    await connection.SendResponseAsync(content: chunkedResponseContent, headers: new[]
                    {
                        new HttpHeaderData("Content-Length", null),
                        new HttpHeaderData("Response-A", "Response-A"),
                        new HttpHeaderData("Transfer-Encoding", "chunked"),
                    });

                    data = await connection.ReadRequestDataAsync();
                    Assert.Equal("GET", data.Method);
                    Assert.Equal(server.Address.PathAndQuery, data.Path);
                    Assert.Equal(GetOriginFromUri(server.Address), data.GetSingleHeaderValue("Host"));
                    Assert.Equal("Request-B", data.GetSingleHeaderValue("Request-B"));
                    await connection.SendResponseAsync(content: chunkedResponseContent, headers: new[]
                    {
                        new HttpHeaderData("Content-Length", null),
                        new HttpHeaderData("Response-B", "Response-B"),
                        new HttpHeaderData("Transfer-Encoding", "chunked")
                    });
                });
        }

        [Fact]
        public async Task Post_WithRequestContent_Success()
        {
            byte[] content = Encoding.ASCII.GetBytes("Hello, World!");

            await RunClientAndServer(
                async uri =>
                {
                    await using HttpConnection connection = await OpenConnection(uri);
                    await using HttpRequestStream stream = await connection.CreateNewRequestStreamAsync();

                    stream.ConfigureRequest(contentLength: content.Length, hasTrailingHeaders: false);
                    stream.WriteRequest(HttpMethod.Post, uri, Primitives.HttpVersion.Version11);
                    stream.WriteHeader("Content-Type", "text/plain");
                    stream.WriteHeader("Content-Length", content.Length.ToString(CultureInfo.InvariantCulture));
                    await stream.WriteContentAsync(content);
                    await stream.CompleteRequestAsync();

                    HttpResponseData response = await ReadResponse(stream);
                    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                },
                async server =>
                {
                    HttpRequestData data = await server.AcceptConnectionSendResponseAndCloseAsync(HttpStatusCode.NoContent);

                    Assert.Equal("POST", data.Method);
                    Assert.Equal(server.Address.PathAndQuery, data.Path);
                    Assert.Equal(GetOriginFromUri(server.Address), data.GetSingleHeaderValue("Host"));
                    Assert.Equal("text/plain", data.GetSingleHeaderValue("Content-Type"));
                    Assert.True(content.SequenceEqual(data.Body));
                });
        }

        [Fact]
        public async Task Post_WithChunkedRequestContent_Success()
        {
            await RunClientAndServer(
                async uri =>
                {
                    await using HttpConnection connection = await OpenConnection(uri);
                    await using HttpRequestStream stream = await connection.CreateNewRequestStreamAsync();

                    stream.ConfigureRequest(contentLength: null, hasTrailingHeaders: false);
                    stream.WriteRequest(HttpMethod.Post, uri, Primitives.HttpVersion.Version11);
                    stream.WriteHeader("Content-Type", "text/plain");
                    stream.WriteHeader("Transfer-Encoding", "chunked");
                    await stream.WriteContentAsync(Encoding.ASCII.GetBytes("Hello,"));
                    await stream.WriteContentAsync(Encoding.ASCII.GetBytes(" "));
                    await stream.WriteContentAsync(Encoding.ASCII.GetBytes("World!"));
                    await stream.WriteContentAsync(Encoding.ASCII.GetBytes(" Also this piece long enough to create hex!"));
                    await stream.CompleteRequestAsync();

                    HttpResponseData response = await ReadResponse(stream);
                    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                },
                async server =>
                {
                    HttpRequestData data = await server.AcceptConnectionSendResponseAndCloseAsync(HttpStatusCode.NoContent);

                    Assert.Equal("POST", data.Method);
                    Assert.Equal(server.Address.PathAndQuery, data.Path);
                    Assert.Equal(GetOriginFromUri(server.Address), data.GetSingleHeaderValue("Host"));
                    Assert.Equal("chunked", data.GetSingleHeaderValue("Transfer-Encoding"));
                    Assert.Equal("text/plain", data.GetSingleHeaderValue("Content-Type"));
                    Assert.Equal("Hello, World! Also this piece long enough to create hex!", Encoding.ASCII.GetString(data.Body));
                });
        }

        [Fact]
        public async Task Post_MultipleWithChunkedRequestContent_Success()
        {
            await RunClientAndServer(
                async uri =>
                {
                    await using HttpConnection connection = await OpenConnection(uri);

                    await using (HttpRequestStream stream = await connection.CreateNewRequestStreamAsync())
                    {
                        stream.ConfigureRequest(contentLength: null, hasTrailingHeaders: false);
                        stream.WriteRequest(HttpMethod.Post, uri, Primitives.HttpVersion.Version11);
                        stream.WriteHeader("Request-A", "Request-A");
                        stream.WriteHeader("Content-Type", "text/plain");
                        stream.WriteHeader("Transfer-Encoding", "chunked");
                        await stream.WriteContentAsync(Encoding.ASCII.GetBytes("Hello,"));
                        await stream.WriteContentAsync(Encoding.ASCII.GetBytes(" "));
                        await stream.WriteContentAsync(Encoding.ASCII.GetBytes("World!"));
                        await stream.CompleteRequestAsync();

                        HttpResponseData response = await ReadResponse(stream);
                        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                    }

                    await using (HttpRequestStream stream = await connection.CreateNewRequestStreamAsync())
                    {
                        stream.ConfigureRequest(contentLength: null, hasTrailingHeaders: false);
                        stream.WriteRequest(HttpMethod.Post, uri, Primitives.HttpVersion.Version11);
                        stream.WriteHeader("Request-B", "Request-B");
                        stream.WriteHeader("Content-Type", "text/plain");
                        stream.WriteHeader("Transfer-Encoding", "chunked");
                        await stream.WriteContentAsync(Encoding.ASCII.GetBytes("Hello,"));
                        await stream.WriteContentAsync(Encoding.ASCII.GetBytes(" "));
                        await stream.WriteContentAsync(Encoding.ASCII.GetBytes("World!"));
                        await stream.CompleteRequestAsync();

                        HttpResponseData response = await ReadResponse(stream);
                        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                    }
                },
                async server =>
                {
                    using GenericLoopbackConnection connection = await server.EstablishGenericConnectionAsync();

                    HttpRequestData data = await connection.ReadRequestDataAsync();
                    Assert.Equal("POST", data.Method);
                    Assert.Equal(server.Address.PathAndQuery, data.Path);
                    Assert.Equal(GetOriginFromUri(server.Address), data.GetSingleHeaderValue("Host"));
                    Assert.Equal("Request-A", data.GetSingleHeaderValue("Request-A"));
                    Assert.Equal("chunked", data.GetSingleHeaderValue("Transfer-Encoding"));
                    Assert.Equal("text/plain", data.GetSingleHeaderValue("Content-Type"));
                    Assert.Equal("Hello, World!", Encoding.ASCII.GetString(data.Body));
                    await connection.SendResponseAsync(HttpStatusCode.NoContent);

                    data = await connection.ReadRequestDataAsync();
                    Assert.Equal("POST", data.Method);
                    Assert.Equal(server.Address.PathAndQuery, data.Path);
                    Assert.Equal(GetOriginFromUri(server.Address), data.GetSingleHeaderValue("Host"));
                    Assert.Equal("Request-B", data.GetSingleHeaderValue("Request-B"));
                    Assert.Equal("chunked", data.GetSingleHeaderValue("Transfer-Encoding"));
                    Assert.Equal("text/plain", data.GetSingleHeaderValue("Content-Type"));
                    Assert.Equal("Hello, World!", Encoding.ASCII.GetString(data.Body));
                    await connection.SendResponseAsync(HttpStatusCode.NoContent);
                });
        }

        sealed class HttpResponseData
        {
            public byte[] Body { get; set; }
            public Version Version { get; set; }
            public HttpStatusCode StatusCode { get; set; }
            public List<HttpHeaderData> Headers { get; } = new List<HttpHeaderData>();
            public List<HttpHeaderData> TrailingHeaders { get; } = new List<HttpHeaderData>();

            public string[] GetHeaderValues(string headerName)
            {
                return Headers.Where(h => h.Name.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                        .Select(h => h.Value)
                        .ToArray();
            }

            public string GetSingleHeaderValue(string headerName)
            {
                string[] values = GetHeaderValues(headerName);
                if (values.Length != 1)
                {
                    throw new Exception(
                        $"Expected single value for {headerName} header, actual count: {values.Length}{Environment.NewLine}" +
                        $"{"\t"}{string.Join(Environment.NewLine + "\t", values)}");
                }

                return values[0];
            }
        }

        private static async Task<HttpResponseData> ReadResponse(HttpRequestStream stream)
        {
            var response = new HttpResponseData();

            await stream.ReadToResponseAsync();
            response.Version = stream.Version;
            response.StatusCode = stream.StatusCode;

            if (await stream.ReadToHeadersAsync())
            {
                while (await stream.ReadNextHeaderAsync())
                {
                    string name = Encoding.ASCII.GetString(stream.HeaderName.Span);
                    string value = Encoding.ASCII.GetString(stream.HeaderValue.Span);
                    response.Headers.Add(new HttpHeaderData(name, value));
                }
            }

            var content = new MemoryStream();

            if (await stream.ReadToContentAsync())
            {
                byte[] readBuffer = new byte[512];
                int readLength;

                while ((readLength = await stream.ReadContentAsync(readBuffer)) != 0)
                {
                    content.Write(readBuffer.AsSpan(0, readLength));
                }
            }

            if (await stream.ReadToTrailingHeadersAsync())
            {
                while (await stream.ReadNextHeaderAsync())
                {
                    string name = Encoding.ASCII.GetString(stream.HeaderName.Span);
                    string value = Encoding.ASCII.GetString(stream.HeaderValue.Span);
                    response.TrailingHeaders.Add(new HttpHeaderData(name, value));
                }
            }

            await stream.ReadToEndOfStreamAsync();
            response.Body = content.ToArray();
            return response;
        }

        private static string GetOriginFromUri(Uri requestUri)
        {
            string host = requestUri.IdnHost;
            int port = requestUri.Port;
            return requestUri.HostNameType == UriHostNameType.IPv6
                ? $"[{host}]:{port}"
                : $"{host}:{port}";
        }

        private static async Task<HttpConnection> OpenConnection(Uri uri)
        {
            Socket socket = null;

            try
            {
                socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(uri.Host, uri.Port);

                var connection = new Http11Connection(new NetworkStream(socket, ownsSocket: true), ownsStream: true);

                socket = null;
                return connection;
            }
            finally
            {
                socket?.Dispose();
            }
        }

        private static async Task RunClientAndServer(Func<Uri, Task> clientFunc, Func<GenericLoopbackServer, Task> serverFunc)
        {
            using GenericLoopbackServer server = new LoopbackServer();

            Task serverTask = serverFunc(server);
            Task clientTask = clientFunc(server.Address);

            await new[] { serverTask, clientTask }.WhenAllOrAnyFailed(millisecondsTimeout: 60_000);
        }
    }
}
