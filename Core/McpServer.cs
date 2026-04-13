// McpServer.cs - Minimal HTTP listener (async) 
using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using System.IO;

public static class McpServer
{
    private static HttpListener _listener;
    private static Task _listenTask;
    private static ExternalEvent _externalEvent;
    private static RequestHandler _handler;

    public static void Start()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://*:5005/mcp/");
            _listener.Start();
            LogError("HttpListener started on http://*:5005/mcp/");
        }
        catch (Exception ex)
        {
            LogError($"HttpListener.Start() FAILED: {ex}");
            throw;
        }

        _handler = new RequestHandler();
        _externalEvent = ExternalEvent.Create(_handler);

        _listenTask = ListenAsync();
    }

    public static void Stop()
    {
        _listener?.Stop();
        _listenTask?.Wait();
    }

    public static void RaiseIfPending()
    {
        if (_handler != null && _handler.HasPending && _externalEvent != null)
        {
            var result = _externalEvent.Raise();
            if (result == ExternalEventRequest.Accepted)
                LogError("RaiseIfPending: re-raised ExternalEvent");
        }
    }

    public static void ProcessPending(UIApplication app)
    {
        if (_handler != null && _handler.HasPending)
        {
            LogError("ProcessPending: calling Execute() directly from Idling");
            try
            {
                _handler.Execute(app);
            }
            catch (Exception ex)
            {
                LogError($"ProcessPending Execute() threw: {ex}");
            }
        }
    }

    private static async Task ListenAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context = null;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogError($"Accept error: {ex}");
                continue;
            }

            _ = ProcessRequestAsync(context);
        }
    }

    private static async Task ProcessRequestAsync(HttpListenerContext context)
    {
        StreamReader reader = null;
        try
        {
            // Handle CORS preflight
            if (string.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                TrySetCors(context.Response);
                context.Response.StatusCode = 200;
                context.Response.Close();
                return;
            }

            reader = new StreamReader(context.Request.InputStream);
            string requestBody = await reader.ReadToEndAsync();

            LogError($"Request received: {requestBody}");
            _handler.SetRequest(requestBody, context);
            var raiseResult = _externalEvent.Raise();
            LogError($"ExternalEvent.Raise() result: {raiseResult}");
        }
        catch (Exception ex)
        {
            LogError($"Process error: {ex}");
            try
            {
                context.Response.StatusCode = 500;
                byte[] buffer = Encoding.UTF8.GetBytes("{\"status\":\"error\"}");
                TrySetCors(context.Response);
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                context.Response.Close();
            }
            catch { }
        }
        finally
        {
            if (reader != null)
                reader.Dispose();
        }
    }

    private static readonly string LogPath = @"C:\Temp\mcp.log";

    public static void Log(string message) => LogError(message);

    private static void LogError(string message)
    {
        try
        {
            Directory.CreateDirectory(@"C:\Temp");
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static void TrySetCors(HttpListenerResponse response)
    {
        try
        {
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
        }
        catch { }
    }
}
