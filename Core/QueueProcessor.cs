using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using Newtonsoft.Json;

/// <summary>
/// Executes queued plans on the Revit main thread via the Idling event.
/// Task.Run is intentionally avoided — the Revit API is only valid on the main thread.
/// </summary>
public static class QueueProcessor
{
    private static bool _started;
    private static bool _hasPending;
    private static DateTime _lastPolled = DateTime.MinValue;
    private const int PollIntervalSeconds = 5;

    public static void Start(UIApplication app)
    {
        _started = true;
    }

    public static void Stop()
    {
        _started = false;
    }

    /// <summary>
    /// Signal that a job was just enqueued so ProcessNext polls immediately.
    /// </summary>
    public static void NotifyJobEnqueued()
    {
        _hasPending = true;
    }

    /// <summary>
    /// Called from the Idling event — runs on the Revit main thread.
    /// Executes at most one pending job per idle tick.
    /// </summary>
    public static void ProcessNext(UIApplication app)
    {
        if (!_started) return;

        // Rate-limit DB polling unless a job was just enqueued
        if (!_hasPending && (DateTime.UtcNow - _lastPolled).TotalSeconds < PollIntervalSeconds)
            return;

        _lastPolled = DateTime.UtcNow;

        string conn = DbConfigHelper.GetConnectionString();
        if (string.IsNullOrEmpty(conn)) return;

        var db = new PostgresDb(conn);
        var (id, plan) = db.DequeuePlan();

        if (id == 0)
        {
            _hasPending = false;
            return;
        }

        McpServer.Log($"QueueProcessor: starting job {id}");
        var planCmd = new PlanExecutorCommand();
        var input = new Dictionary<string, string> { { "steps", plan } };
        try
        {
            var result = planCmd.Execute(app, input);
            string jsonResult = JsonConvert.SerializeObject(result);
            db.SetJobResult(id, "done", jsonResult);
            McpServer.Log($"QueueProcessor: job {id} completed");
        }
        catch (Exception ex)
        {
            McpServer.Log($"QueueProcessor: job {id} failed: {ex.Message}");
            db.SetJobResult(id, "error", JsonConvert.SerializeObject(new { error = ex.Message, stack = ex.ToString() }));
        }
    }
}
