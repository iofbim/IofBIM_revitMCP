// App.cs - Entry point
using System;
using Autodesk.Revit.UI;

public class App : IExternalApplication
{
    public Result OnStartup(UIControlledApplication app)
    {
        try
        {
            McpServer.Start();
            app.Idling += OnIdling;
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Startup Error", $"McpServer failed: {ex.Message}");
            return Result.Failed;
        }
    }

    private void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
        var uiApp = sender as UIApplication;
        if (uiApp != null)
            McpServer.ProcessPending(uiApp);
    }

    public Result OnShutdown(UIControlledApplication app)
    {
        McpServer.Stop();
        QueueProcessor.Stop();
        return Result.Succeeded;
    }
}