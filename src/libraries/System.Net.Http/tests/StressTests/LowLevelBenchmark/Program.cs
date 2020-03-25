using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Primitives;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace LowLevelBenchmark
{
    public class Program
    {
        private const int WarmupMilliseconds = 5_000;
        private const int BenchmarkMilliseconds = 15_000;

        private static readonly byte[] s_getMethod = Encoding.ASCII.GetBytes("GET");
        private static readonly byte[] s_postMethod = Encoding.ASCII.GetBytes("POST");
        private static readonly byte[] s_ContentLength = Encoding.ASCII.GetBytes("Content-Length");
        private static readonly byte[] s_TransferEncoding = Encoding.ASCII.GetBytes("Transfer-Encoding");
        private static readonly byte[] s_TransferEncodingChunked = Encoding.ASCII.GetBytes("chunked");
        private static readonly byte[] s_Accept = Encoding.ASCII.GetBytes("Accept");
        private static readonly byte[] s_AcceptValue = Encoding.ASCII.GetBytes("text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9");
        private static readonly byte[] s_AcceptEncoding = Encoding.ASCII.GetBytes("Accept-Encoding");
        private static readonly byte[] s_AcceptEncodingValue = Encoding.ASCII.GetBytes("zip, deflate, br");
        private static readonly byte[] s_AcceptLanguage = Encoding.ASCII.GetBytes("Accept-Language");
        private static readonly byte[] s_AcceptLanguageValue = Encoding.ASCII.GetBytes("en-US,en;q=0.9");
        private static readonly byte[] s_Cookie = Encoding.ASCII.GetBytes("Cookie");
        private static readonly byte[] s_CookieValue = Encoding.ASCII.GetBytes("user=XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX; _scp=0000000000000.111111111; __utma=22222222.333333333.4444444444.5555555555.6666666666.7; __utmc=88888888; __utmz=99999999.0000000000.1.2.utmcsr=(direct)|utmccn=(direct)|utmcmd=(none); _pk_id.33.4444=ZZZZZZZZZZZZZZZZ.5555555555.6.7777777777.8888888888.");
        private static readonly byte[] s_Referer = Encoding.ASCII.GetBytes("Referer");
        private static readonly byte[] s_RefererValue = Encoding.ASCII.GetBytes("https://slashdot.org/");
        private static readonly byte[] s_SecFetchDest = Encoding.ASCII.GetBytes("Sec-Fetch-Dest");
        private static readonly byte[] s_SecFetchDestValue = Encoding.ASCII.GetBytes("document");
        private static readonly byte[] s_SecFetchMode = Encoding.ASCII.GetBytes("Sec-Fetch-Mode");
        private static readonly byte[] s_SecFetchModeValue = Encoding.ASCII.GetBytes("navigate");
        private static readonly byte[] s_SecFetchSite = Encoding.ASCII.GetBytes("Sec-Fetch-Site");
        private static readonly byte[] s_SecFetchSiteValue = Encoding.ASCII.GetBytes("same-site");
        private static readonly byte[] s_SecFetchUser = Encoding.ASCII.GetBytes("Sec-Fetch-User");
        private static readonly byte[] s_SecFetchUserValue = Encoding.ASCII.GetBytes("?1");
        private static readonly byte[] s_UpgradeInsecureRequests = Encoding.ASCII.GetBytes("Upgrade-Insecure-Requests");
        private static readonly byte[] s_UpgradeInsecureRequestsValue = Encoding.ASCII.GetBytes("1");
        private static readonly byte[] s_UserAgent = Encoding.ASCII.GetBytes("User-Agent");
        private static readonly byte[] s_UserAgentValue = Encoding.ASCII.GetBytes("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/80.0.3987.132 Safari/537.36 Edg/80.0.361.66");

        private static HttpClient s_cpuClient;
        private static System.Diagnostics.Process s_currentProcess = System.Diagnostics.Process.GetCurrentProcess();

        public static async Task Main(string[] args)
        {
            System.Diagnostics.Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { cts.Cancel(); e.Cancel = true; };

            var root = new RootCommand("Benchmarks HttpClient and LowLevel client.");

            var parallelismOption = new Option<int>(new[] { "--parallelism", "-p" }, () => 1, "The number of requests to make in parallel.");
            var loopsOption = new Option<int>(new[] { "--loops", "-l" }, () => -1, "The number of loops to execute in each thread. If not specified, loop for 15 seconds.");
            var uriOption = new Option<string>(new[] { "--uri", "-u" }, "The URI to listen/connect on.") { Required = true };
            var clientOption = new Option<ClientType>(new string[] { "--client", "-c" }, () => ClientType.All, "The HTTP client to use.");
            var benchmarkOption = new Option<string>(new[] { "--benchmark", "-b" }, () => null, "The specific benchmark to run.");

            var loopbackCommand = new Command("loopback");
            loopbackCommand.AddOption(uriOption);
            loopbackCommand.AddOption(parallelismOption);
            loopbackCommand.AddOption(clientOption);
            loopbackCommand.AddOption(benchmarkOption);
            loopbackCommand.Handler = CommandHandler.Create<string, int, ClientType, string>(async (uri, parallelism, client, benchmark) =>
            {
                using IWebHost host = CreateWebHost(uri);
                await host.StartAsync(cts.Token).ConfigureAwait(false);

                try
                {
                    await RunClientAsync(parallelism, loops: -1, client, benchmark, new Uri(uri, UriKind.Absolute), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    await host.StopAsync().ConfigureAwait(false);
                }
            });
            root.AddCommand(loopbackCommand);

            var serverCommand = new Command("server");
            serverCommand.AddOption(uriOption);
            serverCommand.Handler = CommandHandler.Create<string>(async uri =>
            {
                using IWebHost host = CreateWebHost(uri);
                await host.StartAsync(cts.Token).ConfigureAwait(false);

                try
                {
                    await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    await host.StopAsync().ConfigureAwait(false);
                }
            });
            root.AddCommand(serverCommand);

            var clientCommand = new Command("client");
            clientCommand.AddOption(uriOption);
            clientCommand.AddOption(loopsOption);
            clientCommand.AddOption(parallelismOption);
            clientCommand.AddOption(clientOption);
            clientCommand.AddOption(benchmarkOption);
            clientCommand.Handler = CommandHandler.Create<string, int, int, ClientType, string>(async (uri, loops, parallelism, client, benchmark) =>
            {
                try
                {
                    await RunClientAsync(parallelism, loops, client, benchmark, new Uri(uri, UriKind.Absolute), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            });
            root.AddCommand(clientCommand);

            try
            {
                await root.InvokeAsync(args).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
            {
                // ignore.
            }
        }

        private static async Task RunClientAsync(int parallelism, int loops, ClientType clientType, string benchmarkName, Uri serverUri, CancellationToken cancellationToken)
        {
            var matrix =
                from contentSize in new[] { 0, 64, 8192 }
                from methodInfo in typeof(Program).GetMethods(BindingFlags.Public | BindingFlags.Static)
                where methodInfo.ReturnType == typeof(Scenario) && (benchmarkName == null || methodInfo.Name == benchmarkName)
                let scenario = (Scenario)methodInfo.Invoke(null, Array.Empty<object>())
                where (contentSize != 0) == scenario.SupportsMultipleContentSizes
                select (contentSize, scenario, methodInfo.Name);

            var results = new List<(string clientType, string scenarioName, int contentSize, AggregateMeasurement measurement)>();

            foreach ((int contentSize, Scenario scenario, string name) in matrix)
            {
                AggregateMeasurement measurement;

                if (clientType.HasFlag(ClientType.HttpClient))
                {
                    measurement = await RunScenarioHttpClient(parallelism, loops, serverUri, contentSize, scenario, name, cancellationToken).ConfigureAwait(false);
                    results.Add(("HttpClient", name, contentSize, measurement));
                    Console.WriteLine($"{name}({contentSize}) HttpClient:{Environment.NewLine}{measurement}");
                }

                if (clientType.HasFlag(ClientType.LowLevel))
                {
                    measurement = await RunScenarioLowLevel(parallelism, loops, serverUri, contentSize, scenario, name, cancellationToken).ConfigureAwait(false);
                    results.Add(("LowLevel", name, contentSize, measurement));
                    Console.WriteLine($"{name}({contentSize}) LowLevel:{Environment.NewLine}{measurement}");
                }
            }

            await WriteReportAsync(results).ConfigureAwait(false);
        }

        private static async Task WriteReportAsync(List<(string clientType, string scenarioName, int contentSize, AggregateMeasurement measurement)> results)
        {
            var clients = results
                .Select(x => x.clientType)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var scenarios = results
                .Select(x => (x.scenarioName, x.contentSize))
                .Distinct()
                .OrderBy(x => x.scenarioName).ThenBy(x => x.contentSize)
                .ToList();

            var header = new[] { "Scenario" }.Concat(clients);

            var records =
                from scenario in scenarios
                let measurementColumns =
                    from client in clients
                    join result in results on (scenario.scenarioName, scenario.contentSize, client) equals (result.scenarioName, result.contentSize, result.clientType) into clientResults
                    from clientResult in clientResults.DefaultIfEmpty()
                    orderby client
                    select clientResult.measurement.P90.RequestsPerSecond.ToString("N0")
                select new[] { $"{scenario.scenarioName}({scenario.contentSize})" }.Concat(measurementColumns);

            await Ctl.Data.Formats.Csv.WriteAsync("results.csv", new[] { header }.Concat(records), CancellationToken.None).ConfigureAwait(false);
        }

        private static Task<AggregateMeasurement> RunScenarioHttpClient(int parallelism, int loops, Uri serverUri, int contentSize, Scenario scenario, string name, CancellationToken cancellationToken)
        {
            return Benchmark("HttpClient/" + name, parallelism, loops, serverUri, contentSize, scenario, cancellationToken,
                () =>
                {
                    // Disable features to get as close to low-level "no frills" feature set.
                    var handler = new SocketsHttpHandler
                    {
                        Credentials = null,
                        AllowAutoRedirect = false,
                        AutomaticDecompression = DecompressionMethods.None,
                        UseProxy = false
                    };

                    return new HttpMessageInvoker(handler, disposeHandler: true);
                },
                globalState => new ValueTask<int>(0),
                async (client, _, readBuffer, opts) =>
                {
                    opts.SetStartTicks();
                    using (HttpRequestMessage request = scenario.HttpClientFunc(opts))
                    using (HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        opts.SetTicksToResponse();
                        opts.SetTicksToFirstHeader();
                        opts.SetTicksToLastHeader();

                        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        int readBytes = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                        opts.SetTicksToFirstContent();
                        while (readBytes != 0)
                        {
                            readBytes = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                        }
                        opts.SetTicksToLastContent();
                    }
                    opts.SetTicksToEndOfRequest();
                });
        }

        private static Task<AggregateMeasurement> RunScenarioLowLevel(int parallelism, int loops, Uri serverUri, int contentSize, Scenario scenario, string name, CancellationToken cancellationToken)
        {
            return Benchmark("LowLevel/" + name, parallelism, loops, serverUri, contentSize, scenario, cancellationToken,
                () => 0,
                async (globalState) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    await socket.ConnectAsync(serverUri.Host, serverUri.Port).ConfigureAwait(false);
                    socket.NoDelay = true;

                    return new Http11Connection(new NetworkStream(socket, ownsSocket: true), ownsStream: true);
                },
                async (_, connection, readBuffer, opts) =>
                {
                    opts.SetStartTicks();
                    await using (HttpRequestStream request = await connection.CreateNewRequestStreamAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await scenario.LowLevelFunc(request, opts).ConfigureAwait(false);

                        while (true)
                        {
                            switch (await request.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                case HttpReadType.Response:
                                    opts.SetTicksToResponse();
                                    break;
                                case HttpReadType.Headers:
                                    bool hasHeaders = await request.ReadNextHeaderAsync(cancellationToken).ConfigureAwait(false);
                                    opts.SetTicksToFirstHeader();
                                    while (hasHeaders)
                                    {
                                        hasHeaders = await request.ReadNextHeaderAsync(cancellationToken).ConfigureAwait(false);
                                    }
                                    opts.SetTicksToLastHeader();
                                    break;
                                case HttpReadType.Content:
                                    int readBytes = await request.ReadContentAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                                    opts.SetTicksToFirstContent();
                                    while (readBytes != 0)
                                    {
                                        readBytes = await request.ReadContentAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                                    }
                                    opts.SetTicksToLastContent();
                                    break;
                                case HttpReadType.TrailingHeaders:
                                    while (await request.ReadNextHeaderAsync(cancellationToken).ConfigureAwait(false)) ;
                                    break;
                                case HttpReadType.EndOfStream:
                                    opts.SetTicksToEndOfRequest();
                                    return;
                            }
                        }
                    }
                });
        }

        private static async Task<AggregateMeasurement> Benchmark<TGlobalState, TThreadState>(string name, int parallelism, int loops, Uri serverUri, int contentSize, Scenario scenario, CancellationToken cancellationToken, Func<TGlobalState> createGlobalState, Func<TGlobalState, ValueTask<TThreadState>> createThreadState, Func<TGlobalState, TThreadState, byte[], ScenarioOptions, Task> runOnce)
        {
            var measurements = new List<Measurement>();
            TimeSpan clientTotalCpuTime, serverTotalCpuTime;

            TGlobalState globalState = createGlobalState();
            try
            {
                Console.WriteLine($"{name}({contentSize}): executing...");

                TimeSpan serverStartTime = await GetServerProcessTime(serverUri, cancellationToken).ConfigureAwait(false);
                TimeSpan clientStartTime = GetProcessTime();
                Stopwatch sw = Stopwatch.StartNew();

                Task[] threads = new Task[parallelism];
                for (int i = 0; i < threads.Length; ++i)
                {
                    threads[i] = RunThread();
                }
                await Task.WhenAll(threads).ConfigureAwait(false);

                TimeSpan clientEndTime = GetProcessTime();
                TimeSpan serverEndTime = await GetServerProcessTime(serverUri, cancellationToken).ConfigureAwait(false);

                clientTotalCpuTime = clientEndTime - clientStartTime;
                serverTotalCpuTime = serverEndTime - serverStartTime;

                async Task RunThread()
                {
                    byte[] readBuffer = new byte[4096];
                    var opts = new ScenarioOptions(serverUri, scenario.EndPointRelativeUriFunc(contentSize), contentSize, cancellationToken);

                    var threadState = await createThreadState(globalState).ConfigureAwait(false);
                    try
                    {
                        long elapsed;
                        int count = 0;

                        do
                        {
                            await runOnce(globalState, threadState, readBuffer, opts).ConfigureAwait(false);

                            elapsed = sw.ElapsedMilliseconds;
                            if (loops != -1 || (elapsed > WarmupMilliseconds && elapsed < BenchmarkMilliseconds))
                            {
                                lock (measurements)
                                {
                                    measurements.Add(new Measurement(opts, parallelism));
                                }
                            }
                        }
                        while (loops == -1 ? elapsed < BenchmarkMilliseconds + 1000 : ++count < loops);
                    }
                    finally
                    {
                        switch (threadState)
                        {
                            case IAsyncDisposable ad: await ad.DisposeAsync().ConfigureAwait(false); break;
                            case IDisposable d: d.Dispose(); break;
                        }
                    }
                }
            }
            finally
            {
                switch (globalState)
                {
                    case IAsyncDisposable ad: await ad.DisposeAsync().ConfigureAwait(false); break;
                    case IDisposable d: d.Dispose(); break;
                }
            }

            return new AggregateMeasurement(measurements, clientTotalCpuTime, serverTotalCpuTime);
        }

        private static IWebHost CreateWebHost(string uri)
        {
            return new WebHostBuilder()
                .UseKestrel()
                .UseUrls(uri)
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(routeBuilder =>
                    {
                        routeBuilder.MapGet("/getnocontent", context =>
                        {
                            context.Response.StatusCode = 204;
                            context.Response.ContentLength = 0;
                            return Task.CompletedTask;
                        });

                        routeBuilder.MapGet("/getcontent", async context =>
                        {
                            int contentSize = int.Parse(context.Request.Query["size"].Single());
                            context.Response.StatusCode = 200;
                            context.Response.ContentLength = contentSize;

                            await context.Response.StartAsync().ConfigureAwait(false);
                            PipeWriter writer = context.Response.BodyWriter;
                            while (contentSize != 0)
                            {
                                Memory<byte> memory = writer.GetMemory();
                                int commitSize = Math.Min(contentSize, memory.Length);
                                writer.Advance(commitSize);
                                await writer.FlushAsync().ConfigureAwait(false);
                                contentSize -= commitSize;
                            }
                        });

                        routeBuilder.MapGet("/getchunkedcontent", async context =>
                        {
                            int contentSize = int.Parse(context.Request.Query["size"].Single());
                            context.Response.StatusCode = 200;
                            context.Response.ContentLength = null;

                            await context.Response.StartAsync().ConfigureAwait(false);
                            PipeWriter writer = context.Response.BodyWriter;
                            while (contentSize != 0)
                            {
                                Memory<byte> memory = writer.GetMemory();
                                int commitSize = Math.Min(contentSize, memory.Length);
                                writer.Advance(commitSize);
                                await writer.FlushAsync().ConfigureAwait(false);
                                contentSize -= commitSize;
                            }
                        });

                        routeBuilder.MapGet("/getrealisticcontent", async context =>
                        {
                            int contentSize = int.Parse(context.Request.Query["size"].Single());
                            context.Response.StatusCode = 200;
                            context.Response.ContentLength = null;
                            context.Response.ContentType = "text/html; charset=utf-8";
                            context.Response.Headers.Add("Date", "Mon, 16 Mar 2020 10:27:48 GMT");
                            context.Response.Headers.Add("Pragma", "no-cache");
                            context.Response.Headers.Add("Server", "nginx/1.14.0 (Ubuntu)");
                            context.Response.Headers.Add("Strict-Transport-Security", "max-age=31536000");
                            context.Response.Headers.Add("x-xrds-location", "https://slashdot.org/slashdot.xrds");

                            await context.Response.StartAsync().ConfigureAwait(false);
                            PipeWriter writer = context.Response.BodyWriter;
                            while (contentSize != 0)
                            {
                                Memory<byte> memory = writer.GetMemory();
                                int commitSize = Math.Min(Math.Min(contentSize, memory.Length), 4096);
                                writer.Advance(commitSize);
                                await writer.FlushAsync().ConfigureAwait(false);
                                contentSize -= commitSize;
                            }
                        });

                        routeBuilder.MapPost("/postcontent", async context =>
                        {
                            PipeReader reader = context.Request.BodyReader;
                            ReadResult res;
                            do
                            {
                                res = await reader.ReadAsync().ConfigureAwait(false);
                                reader.AdvanceTo(res.Buffer.End);
                            }
                            while (!res.IsCompleted);

                            context.Response.StatusCode = 204;
                            context.Response.ContentLength = 0;
                        });

                        routeBuilder.MapGet("/cpu", async context =>
                        {
                            ReadOnlyMemory<byte> content = Encoding.UTF8.GetBytes(GetProcessTime().ToString());
                            context.Response.StatusCode = 200;
                            context.Response.ContentLength = content.Length;
                            context.Response.ContentType = "text/plain";

                            await context.Response.StartAsync().ConfigureAwait(false);
                            PipeWriter writer = context.Response.BodyWriter;
                            while (content.Length != 0)
                            {
                                Memory<byte> memory = writer.GetMemory(content.Length);
                                int commitSize = Math.Min(content.Length, memory.Length);
                                content.Slice(0, commitSize).CopyTo(memory);
                                writer.Advance(commitSize);
                                await writer.FlushAsync().ConfigureAwait(false);
                                content = content.Slice(commitSize);
                            }
                        });
                    });
                }).Build();
        }

        private static TimeSpan GetProcessTime()
        {
            GC.Collect();
            return s_currentProcess.TotalProcessorTime;
        }

        private static async Task<TimeSpan> GetServerProcessTime(Uri serverUri, CancellationToken cancellationToken)
        {
            s_cpuClient ??= new HttpClient() { BaseAddress = serverUri };
            string response = await s_cpuClient.GetStringAsync("/cpu", cancellationToken).ConfigureAwait(false);
            return TimeSpan.ParseExact(response, "c", CultureInfo.InvariantCulture);
        }

        public static Scenario GetNoContent() => new Scenario
        {
            EndPointRelativeUriFunc = _ => new Uri("/getnocontent", UriKind.Relative),
            SupportsMultipleContentSizes = false,
            HttpClientFunc = o => new HttpRequestMessage
            {
                Version = System.Net.HttpVersion.Version11,
                Method = HttpMethod.Get,
                RequestUri = o.Uri
            },
            LowLevelFunc = (stream, o) =>
            {
                stream.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                stream.WriteRequest(o.Origin, s_getMethod, o.PathAndQuery, System.Net.Http.Primitives.HttpVersion.Version11);
                return stream.CompleteRequestAsync(o.CancellationToken);
            }
        };

        public static Scenario GetContent() => new Scenario
        {
            EndPointRelativeUriFunc = contentSize => new Uri($"/getcontent?size={contentSize}", UriKind.Relative),
            SupportsMultipleContentSizes = true,
            HttpClientFunc = o => new HttpRequestMessage
            {
                Version = System.Net.HttpVersion.Version11,
                Method = HttpMethod.Get,
                RequestUri = o.Uri
            },
            LowLevelFunc = (stream, o) =>
            {
                stream.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                stream.WriteRequest(o.Origin, s_getMethod, o.PathAndQuery, System.Net.Http.Primitives.HttpVersion.Version11);
                return stream.CompleteRequestAsync(o.CancellationToken);
            }
        };

        public static Scenario GetChunkedContent() => new Scenario
        {
            EndPointRelativeUriFunc = contentSize => new Uri($"/getchunkedcontent?size={contentSize}", UriKind.Relative),
            SupportsMultipleContentSizes = true,
            HttpClientFunc = o => new HttpRequestMessage
            {
                Version = System.Net.HttpVersion.Version11,
                Method = HttpMethod.Get,
                RequestUri = o.Uri
            },
            LowLevelFunc = (stream, o) =>
            {
                stream.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                stream.WriteRequest(o.Origin, s_getMethod, o.PathAndQuery, System.Net.Http.Primitives.HttpVersion.Version11);
                return stream.CompleteRequestAsync(o.CancellationToken);
            }
        };

        public static Scenario GetRealisticChunkedContent() => new Scenario
        {
            EndPointRelativeUriFunc = contentSize => new Uri($"/getrealisticcontent?size={contentSize}", UriKind.Relative),
            SupportsMultipleContentSizes = true,
            HttpClientFunc = o =>
            {
                var req = new HttpRequestMessage
                {
                    Version = System.Net.HttpVersion.Version11,
                    Method = HttpMethod.Get,
                    RequestUri = o.Uri
                };

                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9");
                req.Headers.TryAddWithoutValidation("Accept-Encoding", "zip, deflate, br");
                req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                req.Headers.TryAddWithoutValidation("Cookie", "user=XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX; _scp=0000000000000.111111111; __utma=22222222.333333333.4444444444.5555555555.6666666666.7; __utmc=88888888; __utmz=99999999.0000000000.1.2.utmcsr=(direct)|utmccn=(direct)|utmcmd=(none); _pk_id.33.4444=ZZZZZZZZZZZZZZZZ.5555555555.6.7777777777.8888888888.");
                req.Headers.TryAddWithoutValidation("Referer", "https://slashdot.org/");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
                req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/80.0.3987.132 Safari/537.36 Edg/80.0.361.66");

                return req;
            },
            LowLevelFunc = (stream, o) =>
            {
                stream.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                stream.WriteRequest(o.Origin, s_getMethod, o.PathAndQuery, System.Net.Http.Primitives.HttpVersion.Version11);
                stream.WriteHeader(s_Accept, s_AcceptValue);
                stream.WriteHeader(s_AcceptEncoding, s_AcceptEncodingValue);
                stream.WriteHeader(s_AcceptLanguage, s_AcceptLanguageValue);
                stream.WriteHeader(s_Cookie, s_CookieValue);
                stream.WriteHeader(s_Referer, s_RefererValue);
                stream.WriteHeader(s_SecFetchDest, s_SecFetchDestValue);
                stream.WriteHeader(s_SecFetchMode, s_SecFetchModeValue);
                stream.WriteHeader(s_SecFetchSite, s_SecFetchSiteValue);
                stream.WriteHeader(s_SecFetchUser, s_SecFetchUserValue);
                stream.WriteHeader(s_UpgradeInsecureRequests, s_UpgradeInsecureRequestsValue);
                stream.WriteHeader(s_UserAgent, s_UserAgentValue); 
                return stream.CompleteRequestAsync(o.CancellationToken);
            }
        };

        public static Scenario PostContent() => new Scenario
        {
            EndPointRelativeUriFunc = contentSize => new Uri($"/postcontent", UriKind.Relative),
            SupportsMultipleContentSizes = true,
            HttpClientFunc = o => new HttpRequestMessage
            {
                Version = System.Net.HttpVersion.Version11,
                Method = HttpMethod.Post,
                RequestUri = o.Uri,
                Content = new ByteArrayContent(o.Content)
            },
            LowLevelFunc = async (stream, o) =>
            {
                stream.ConfigureRequest(contentLength: o.Content.Length, hasTrailingHeaders: false);
                stream.WriteRequest(o.Origin, s_postMethod, o.PathAndQuery, System.Net.Http.Primitives.HttpVersion.Version11);
                stream.WriteHeader(s_ContentLength, o.ContentLengthHeader);
                await stream.WriteContentAsync(o.Content, o.CancellationToken).ConfigureAwait(false);
                await stream.CompleteRequestAsync(o.CancellationToken).ConfigureAwait(false);
            }
        };

        public static Scenario PostChunkedContent() => new Scenario
        {
            EndPointRelativeUriFunc = contentSize => new Uri($"/postcontent", UriKind.Relative),
            SupportsMultipleContentSizes = true,
            HttpClientFunc = o => new HttpRequestMessage
            {
                Version = System.Net.HttpVersion.Version11,
                Method = HttpMethod.Post,
                RequestUri = o.Uri,
                Content = new ByteArrayContentWithoutLength(o.Content)
            },
            LowLevelFunc = async (stream, o) =>
            {
                stream.ConfigureRequest(contentLength: null, hasTrailingHeaders: false);
                stream.WriteRequest(o.Origin, s_postMethod, o.PathAndQuery, System.Net.Http.Primitives.HttpVersion.Version11);
                stream.WriteHeader(s_TransferEncoding, s_TransferEncodingChunked);

                ReadOnlyMemory<byte> sendBuffer = o.Content;
                while (sendBuffer.Length != 0)
                {
                    int sendLength = Math.Min(4096, sendBuffer.Length);
                    await stream.WriteContentAsync(sendBuffer.Slice(0, sendLength), o.CancellationToken).ConfigureAwait(false);
                    sendBuffer = sendBuffer.Slice(sendLength);
                }

                await stream.CompleteRequestAsync(o.CancellationToken).ConfigureAwait(false);
            }
        };
    }

    internal sealed class ByteArrayContentWithoutLength : HttpContent
    {
        private readonly byte[] _content;

        public ByteArrayContentWithoutLength(byte[] content)
        {
            _content = content;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context, CancellationToken cancellationToken)
        {
            ReadOnlyMemory<byte> sendBuffer = _content;

            while (sendBuffer.Length != 0)
            {
                int sendLength = Math.Min(4096, sendBuffer.Length);
                await stream.WriteAsync(sendBuffer.Slice(0, sendLength), cancellationToken).ConfigureAwait(false);
                sendBuffer = sendBuffer.Slice(sendLength);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    public sealed class Scenario
    {
        public Func<int, Uri> EndPointRelativeUriFunc { get; set; }
        public bool SupportsMultipleContentSizes { get; set; }
        public Func<ScenarioOptions, HttpRequestMessage> HttpClientFunc { get; set; }
        public Func<HttpRequestStream, ScenarioOptions, ValueTask> LowLevelFunc { get; set; }
    }

    public sealed class ScenarioOptions
    {
        public Uri Uri { get; }

        public byte[] Origin { get; }
        public byte[] PathAndQuery { get; }
        public byte[] ContentLengthHeader { get; }

        public byte[] Content { get; }
        public CancellationToken CancellationToken { get; }

        public long StartTicks { get; private set; }
        public long TicksToResponse { get; private set; }
        public long? TicksToFirstHeader { get; private set; }
        public long? TicksToLastHeader { get; private set; }
        public long? TicksToFirstContent { get; private set; }
        public long? TicksToLastContent { get; private set; }
        public long TicksToEndOfRequest { get; private set; }

        public ScenarioOptions(Uri serverUri, Uri relativeUri, int contentSize, CancellationToken cancellationToken)
        {
            Uri uri = new Uri(serverUri, relativeUri);

            string origin =
                uri.HostNameType == UriHostNameType.IPv6
                ? $"[{uri.IdnHost}]:{uri.Port}"
                : $"{uri.IdnHost}:{uri.Port}";

            CancellationToken = cancellationToken;
            Uri = uri;
            Origin = Encoding.ASCII.GetBytes(origin);
            PathAndQuery = Encoding.ASCII.GetBytes(uri.PathAndQuery);
            Content = new byte[contentSize];
            ContentLengthHeader = Encoding.ASCII.GetBytes(contentSize.ToString(CultureInfo.InvariantCulture));
        }

        public void SetStartTicks()
        {
            StartTicks = Stopwatch.GetTimestamp();
            TicksToFirstHeader = null;
            TicksToLastHeader = null;
            TicksToFirstContent = null;
            TicksToLastContent = null;
        }

        public void SetTicksToResponse() => TicksToResponse = Stopwatch.GetTimestamp();
        public void SetTicksToFirstHeader() => TicksToFirstHeader = Stopwatch.GetTimestamp();
        public void SetTicksToLastHeader() => TicksToLastHeader = Stopwatch.GetTimestamp();
        public void SetTicksToFirstContent() => TicksToFirstContent = Stopwatch.GetTimestamp();
        public void SetTicksToLastContent() => TicksToLastContent = Stopwatch.GetTimestamp();
        public void SetTicksToEndOfRequest() => TicksToEndOfRequest = Stopwatch.GetTimestamp();
    }

    internal sealed class Measurement
    {
        public double ResponseTime { get; }
        public double? FirstHeaderTime { get; }
        public double? HeadersCompleteTime { get; }
        public double? FirstContentTime { get; }
        public double? ContentCompleteTime { get; }
        public double RequestCompleteTime { get; }
        public double RequestsPerSecond { get; }

        public Measurement(ScenarioOptions opts, int parallelism)
        {
            long start = opts.StartTicks;
            double rFrequency = 1000000.0 / Stopwatch.Frequency;
            ResponseTime = (opts.TicksToResponse - start) * rFrequency;
            FirstHeaderTime = (opts.TicksToFirstHeader - start) * rFrequency;
            HeadersCompleteTime = (opts.TicksToLastHeader - start) * rFrequency;
            FirstContentTime = (opts.TicksToFirstContent - start) * rFrequency;
            ContentCompleteTime = (opts.TicksToLastContent - start) * rFrequency;
            RequestCompleteTime = (opts.TicksToEndOfRequest - start) * rFrequency;
            RequestsPerSecond = parallelism * 1000000.0 / RequestCompleteTime;
        }

        public override string ToString() => $"RPS:{RequestsPerSecond:N0} (R:{ResponseTime:N0} HS:{FirstHeaderTime:N0} HE:{HeadersCompleteTime:N0} CS:{FirstContentTime:N0} CE:{ContentCompleteTime:N0} T:{RequestCompleteTime:N0})";
    }

    internal sealed class AggregateMeasurement
    {
        public Measurement P50 { get; }
        public Measurement P90 { get; }
        public Measurement P99 { get; }
        public Measurement P999 { get; }
        public int MeasurementCount { get; }
        public TimeSpan ClientTotalCpuTime { get; }
        public TimeSpan ServerTotalCpuTime { get; }

        public AggregateMeasurement(List<Measurement> measurements, TimeSpan clientTotalCpuTime, TimeSpan serverTotalCpuTime)
        {
            measurements.Sort((x, y) => x.RequestCompleteTime.CompareTo(y.RequestCompleteTime));

            P50 = measurements[(int)(measurements.Count * 0.5)];
            P90 = measurements[(int)(measurements.Count * 0.9)];
            P99 = measurements[(int)(measurements.Count * 0.99)];
            P999 = measurements[(int)(measurements.Count * 0.999)];
            MeasurementCount = measurements.Count;
            ClientTotalCpuTime = clientTotalCpuTime;
            ServerTotalCpuTime = serverTotalCpuTime;
        }

        public override string ToString() => $"P50: {P50}{Environment.NewLine}P90: {P90}{Environment.NewLine}P99: {P99}{Environment.NewLine}P999: {P999}{Environment.NewLine}Measurement Count: {MeasurementCount:N0}{Environment.NewLine}Client CPU time: {ClientTotalCpuTime.TotalSeconds:N0}s ({ClientTotalCpuTime.TotalSeconds/(ClientTotalCpuTime+ServerTotalCpuTime).TotalSeconds:P}){Environment.NewLine}Server CPU time: {ServerTotalCpuTime.TotalSeconds:N0}s ({ServerTotalCpuTime.TotalSeconds / (ClientTotalCpuTime + ServerTotalCpuTime).TotalSeconds:P}){Environment.NewLine}";
    }

    [Flags]
    enum ClientType
    {
        HttpClient = 1,
        LowLevel = 2,
        All = HttpClient | LowLevel,
    };

    internal sealed class LoggingStream : Stream
    {
        static byte[] s_flushing = Encoding.ASCII.GetBytes("{{{flushing}}}");
        static long s_counter;
        private readonly Stream _baseStream, _readLogStream, _writeLogStream;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public LoggingStream(Stream baseStream)
        {
            long id = Interlocked.Increment(ref s_counter);

            _baseStream = baseStream;
            _readLogStream = File.Create($"reads_{id}.txt", 8192, FileOptions.Asynchronous);
            _writeLogStream = File.Create($"writes_{id}.txt", 8192, FileOptions.Asynchronous);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _baseStream.Dispose();
                _readLogStream.Dispose();
                _writeLogStream.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return _writeLogStream.WriteAsync(s_flushing, cancellationToken).AsTask();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int byteCount = await _baseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _readLogStream.WriteAsync(buffer.Slice(0, byteCount), cancellationToken).ConfigureAwait(false);
            return byteCount;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _writeLogStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _baseStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
