using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace V380Decoder.src
{
    public class WebServer
    {
        private readonly V380Client client;
        private readonly int httpPort;
        private readonly int rtspPort;
        private readonly bool enableApi;
        private readonly bool enableOnvif;
        private WebApplication app;
        private Task runTask;
        public WebServer(int httpPort, int rtspPort, V380Client client, bool enableApi, bool enableOnvif)
        {
            this.httpPort = httpPort;
            this.rtspPort = rtspPort;
            this.client = client;
            this.enableApi = enableApi;
            this.enableOnvif = enableOnvif;
        }

        public async Task Start()
                {
                    string ipAddress = NetworkHelper.GetLocalIPAddress();
                    var builder = WebApplication.CreateBuilder();
                    builder.WebHost.UseUrls($"http://*:{httpPort}");

                    builder.Logging.ClearProviders();
                    builder.Services.ConfigureHttpJsonOptions(options =>
                    {
                        options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
                    });

                    app = builder.Build();

                    Console.Error.WriteLine($"[SNAPSHOT] http://{ipAddress}:{httpPort}/snapshot");
                    app.MapGet("/snapshot", (HttpContext ctx) =>
                    {
                        var jpeg = client.snapshotManager.GetSnapshot(timeoutMs: 5000);

                        if (jpeg == null || jpeg.Length == 0)
                        {
                            return Results.Problem(
                                "No snapshot available. Ensure stream is running",
                                statusCode: 503
                            );
                        }

                        ctx.Response.Headers["Cache-Control"] = "no-cache";

                        return Results.File(jpeg, "image/jpeg");
                    });

                    Console.Error.WriteLine($"[MJPEG] http://{ipAddress}:{httpPort}/stream.mjpg");
                    app.MapGet("/stream.mjpg", async (HttpContext ctx) =>
                    {
                        ctx.Response.Headers["Cache-Control"] = "no-cache";
                        ctx.Response.Headers["Pragma"] = "no-cache";
                        ctx.Response.Headers["Connection"] = "close";
                        ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";

                        long lastVersion = -1;

                        while (!ctx.RequestAborted.IsCancellationRequested)
                        {
                            byte[] jpeg;
                            long version;

                            if (!client.snapshotManager.TryGetCachedSnapshot(out jpeg, out version, out _))
                            {
                                jpeg = client.snapshotManager.GetSnapshot(timeoutMs: 1000);
                                client.snapshotManager.TryGetCachedSnapshot(out jpeg, out version, out _);
                            }

                            if (version != lastVersion && jpeg != null && jpeg.Length > 0)
                            {
                                await ctx.Response.WriteAsync("--frame\r\n");
                                await ctx.Response.WriteAsync("Content-Type: image/jpeg\r\n");
                                await ctx.Response.WriteAsync($"Content-Length: {jpeg.Length}\r\n\r\n");
                                await ctx.Response.Body.WriteAsync(jpeg, 0, jpeg.Length, ctx.RequestAborted);
                                await ctx.Response.WriteAsync("\r\n");
                                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                                lastVersion = version;
                            }

                            await Task.Delay(66, ctx.RequestAborted);
                        }
                    });

                    if (enableApi)
                    {
                        Console.Error.WriteLine($"[WEB] http://{ipAddress}:{httpPort}");
                        Console.Error.WriteLine($"[API] http://{ipAddress}:{httpPort}/api/");

                        app.MapGet("/", () => Results.Content(WebPage.GetHtml(), "text/html"));

                        app.MapPost("/api/ptz/right", () => { client.PtzRight(); LogUtils.debug("[API] PTZ Right"); return Results.Ok(); });
                        app.MapPost("/api/ptz/left", () => { client.PtzLeft(); LogUtils.debug("[API] PTZ Left"); return Results.Ok(); });
                        app.MapPost("/api/ptz/up", () => { client.PtzUp(); LogUtils.debug("[API] PTZ Up"); return Results.Ok(); });
                        app.MapPost("/api/ptz/down", () => { client.PtzDown(); LogUtils.debug("[API] PTZ Down"); return Results.Ok(); });
                        app.MapPost("/api/ptz/stop", () => { client.PtzStop(); LogUtils.debug("[API] PTZ Stop"); return Results.Ok(); });

                        app.MapPost("/api/light/on", () => { client.LightOn(); LogUtils.debug("[API] Light On"); return Results.Ok(); });
                        app.MapPost("/api/light/off", () => { client.LightOff(); LogUtils.debug("[API] Light Off"); return Results.Ok(); });
                        app.MapPost("/api/light/auto", () => { client.LightAuto(); LogUtils.debug("[API] Light Auto"); return Results.Ok(); });

                        app.MapPost("/api/image/color", () => { client.ImageColor(); LogUtils.debug("[API] Image Color"); return Results.Ok(); });
                        app.MapPost("/api/image/bw", () => { client.ImageBW(); LogUtils.debug("[API] Image B&W"); return Results.Ok(); });
                        app.MapPost("/api/image/auto", () => { client.ImageAuto(); LogUtils.debug("[API] Image Auto"); return Results.Ok(); });
                        app.MapPost("/api/image/flip", () => { client.ImageFlip(); LogUtils.debug("[API] Image Flip"); return Results.Ok(); });

                        app.MapGet("/api/status", () => Results.Ok(new StatusResponse
                        {
                            status = "running",
                            timestamp = DateTime.Now
                        }));
                    }

                    if (enableOnvif)
                    {
                        Console.Error.WriteLine($"[ONVIF] http://{ipAddress}:{httpPort}/onvif/device_service");

                        app.MapPost("/onvif/device_service", async (HttpContext ctx) =>
                            await HandleOnvif(ctx));

                        app.MapPost("/onvif/media_service", async (HttpContext ctx) =>
                            await HandleOnvif(ctx));

                        app.MapPost("/onvif/ptz_service", async (HttpContext ctx) =>
                            await HandleOnvif(ctx));

                        app.MapPost("/onvif/imaging_service", async (HttpContext ctx) =>
                            await HandleOnvif(ctx));
                    }

                    LogUtils.debug($"[WEBSERVER] Starting on port {httpPort}...");
                    runTask = app.RunAsync();
                    LogUtils.debug($"[WEBSERVER] Started successfully - waiting for task to complete");
                    // Give Kestrel a moment to bind
                    await Task.Delay(100);
                    LogUtils.debug($"[WEBSERVER] After delay, task status: {runTask.Status}");
                }

        private async Task HandleOnvif(HttpContext ctx)
        {
            string body = "";
            using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            string soapAction = ctx.Request.Headers["SOAPAction"].ToString();
            string contentType = ctx.Request.Headers["Content-Type"].ToString();
            var ctMatch = Regex.Match(contentType, @"action=""([^""]+)""", RegexOptions.IgnoreCase);
            string rawAction = soapAction != "" ? soapAction
                             : ctMatch.Success ? ctMatch.Groups[1].Value
                             : "";

            string action = rawAction.TrimEnd('/').Split('/').Last();
            if (action.StartsWith("wsdl", StringComparison.OrdinalIgnoreCase) && action.Length > 4)
                action = action.Substring(4);


            string resp = OnvifHandler.Handle(action, body, ctx, client, httpPort, rtspPort);

            LogUtils.debug($"[ONVIF] response: {(resp.Length > 300 ? resp[..300] + "..." : resp)}");

            ctx.Response.ContentType = "application/soap+xml; charset=utf-8";
            await ctx.Response.WriteAsync(resp);
        }

        public void Stop()
        {
            if (app == null)
                return;

            try
            {
                app.StopAsync().Wait(5000);
                runTask?.Wait(5000);
            }
            catch
            {
            }
            finally
            {
                app.DisposeAsync().AsTask().Wait(5000);
            }
        }
    }
}
