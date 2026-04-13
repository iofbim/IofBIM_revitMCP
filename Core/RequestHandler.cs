// RequestHandler.cs - Updated to support ExecutePlan
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System;

public class RequestHandler : IExternalEventHandler
{
    private readonly ConcurrentQueue<(string body, HttpListenerContext ctx)> _requests
        = new ConcurrentQueue<(string body, HttpListenerContext ctx)>();

    public static readonly Dictionary<string, ICommand> CommandMap = new Dictionary<string, ICommand>
    {
        // Plans
        { "Plan.Enqueue", new EnqueuePlanCommand() },
        { "Plan.Execute", new PlanExecutorCommand() },

        // Database
        { "Db.Query", new QuerySqlCommand()},
        { "Db.SyncModel", new DbSyncModelCommand() },
        { "Db.Summary", new DbSummaryCommand() },

        // Model / Tools
        { "Model.GetContext", new ListModelContextCommand()},
        { "Tools.CaptureState", new CaptureToolStateCommand()},

        // Categories / Types / Elements / Parameters
        { "Types.List", new ListFamiliesAndTypesCommand()},
        { "Elements.List", new ListElementsCommand() },
        { "Elements.Modify", new ModifyElementsCommand() },
        { "Parameters.ListForElements", new ListElementParametersCommand() },
        { "Parameters.ListForCategory", new ListCategoryParametersCommand() },
        { "Parameters.CreateShared", new NewSharedParameterCommand() },
        { "Elements.FilterByParameter", new FilterByParameterCommand() },

        // Views / Sheets / Schedules / Filters
        { "Views.List", new ListViewsCommand()},
        { "Views.PlaceOnSheet", new PlaceViewsOnSheetCommand() },
        { "Sheets.List", new ListSheetsCommand()},
        { "Sheets.Create" , new CreateSheetCommand() },
        { "Schedules.List", new ListSchedulesCommand()},
        { "Filters.AddToView", new AddViewFilterCommand()},

        // Links
        { "Links.List", new ListLinkedDocumentsCommand()},

        // Export
        { "Export.ToJson" , new ExportToJsonCommand() },

        // Proxy helpers (limited/local only)
        { "Proxy.N8nChat", new ProxyN8nChatCommand() },

        // UI helpers
        { "Ui.PushEvent", new UiPushEventCommand() }
    };

    public bool HasPending => !_requests.IsEmpty;

    public void SetRequest(string body, HttpListenerContext context)
    {
        _requests.Enqueue((body, context));
    }

    public void Execute(UIApplication app)
    {
        try { McpServer.Log($"RequestHandler.Execute() fired, queue size: {_requests.Count}"); } catch { }
        try
        {
        QueueProcessor.Start(app);
        while (_requests.TryDequeue(out var req))
        {
            var requestBody = req.body;
            var context = req.ctx;

            var doc = app.ActiveUIDocument?.Document;
            var response = new Dictionary<string, object>();

        try
        {
            var request = JsonConvert.DeserializeObject<Dictionary<string, string>>(requestBody);

            if (request == null || !request.ContainsKey("action"))
            {
                response["status"] = "error";
                response["message"] = $"Missing or empty request body. Received: {requestBody}";
            }
            else if (request.ContainsKey("action") && CommandMap.ContainsKey(request["action"]))
            {
                response = CommandMap[request["action"].ToString()].Execute(app, request);
            }
            else if (request.ContainsKey("action") && request["action"] == "RunPython")
            {
                ScriptEngine engine = Python.CreateEngine();
                ScriptScope scope = engine.CreateScope();

                scope.SetVariable("doc", doc);
                scope.SetVariable("uidoc", app.ActiveUIDocument);
                scope.SetVariable("FilteredElementCollector", typeof(FilteredElementCollector));
                scope.SetVariable("Wall", typeof(Wall));
                scope.SetVariable("BuiltInCategory", typeof(BuiltInCategory));
                scope.SetVariable("ShowTaskDialog", new Action<string, string>(UiHelpers.ShowTaskDialog));
                scope.SetVariable("get_elements", new Func<Document, string, List<Element>>(RevitHelpers.GetElementsByCategory));
                scope.SetVariable("set_parameter", new Action<Element, string, string>(RevitHelpers.SetParameter));
                scope.SetVariable("capture_tool_state", new Func<UIDocument, Dictionary<string, object>>(RevitHelpers.CaptureToolState));

                string script = request.ContainsKey("script") ? request["script"] : "print('No script provided')";

                var output = new StringWriter();
                engine.Runtime.IO.SetOutput(new MemoryStream(), output);

                engine.Execute(script, scope);
                response["status"] = "executed";
                response["output"] = output.ToString();
            }
            else
            {
                response["status"] = "unknown_action";
            }
        }
        catch (Exception ex)
        {
            response["status"] = "error";
            response["message"] = ex.Message;
            response["stack"] = ex.ToString();
        }

        string json = JsonConvert.SerializeObject(response);
        byte[] buffer = Encoding.UTF8.GetBytes(json);
            // CORS for browser-based dashboards
            try
            {
                context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                context.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
                context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
            }
            catch { }
            context.Response.ContentType = "application/json";
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.Close();
        }
        }
        catch (Exception ex)
        {
            McpServer.Log($"Execute() outer EXCEPTION: {ex}");
        }
    }

    public string GetName() => "MCP Request Handler";
}



