using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

public class ListSchedulesCommand : ICommand
{
    public Dictionary<string, object> Execute(UIApplication app, Dictionary<string, string> input)
    {
        var response = new Dictionary<string, object>();
        var doc = app.ActiveUIDocument?.Document;
        if (doc == null)
        {
            response["status"] = "error";
            response["message"] = "No active document.";
            return response;
        }

        var scheds = new List<Dictionary<string, object>>();
        try
        {
            string conn = DbConfigHelper.GetConnectionString(input);
            if (string.IsNullOrEmpty(conn))
            {
                response["status"] = "error";
                response["message"] = "No connection string found. " + DbConfigHelper.GetHelpMessage();
                return response;
            }

            BatchedPostgresDb db = new BatchedPostgresDb(conn);
            DateTime lastSaved = System.IO.File.GetLastWriteTime(doc.PathName);
            if (db.GetModelLastSaved(doc.PathName) == lastSaved)
            {
                response["status"] = "up_to_date";
                return response;
            }

            var col = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>();
            foreach (var sch in col)
            {
                var item = new Dictionary<string, object>();
                item["id"] = sch.Id.Value;
                item["guid"] = sch.UniqueId;
                item["name"] = sch.Name;
                item["category"] = sch.Definition?.CategoryId != null ? doc.GetElement(sch.Definition.CategoryId)?.Name : string.Empty;
                item["doc_id"] = doc.PathName;
                scheds.Add(item);

                db.UpsertSchedule((int)sch.Id.Value, Guid.Empty, sch.Name, item["category"].ToString(), doc.PathName, lastSaved);
            }
            db.UpsertModelInfo(doc.PathName, doc.Title, ParseGuid(doc.ProjectInformation.UniqueId), lastSaved,
                null, null);
            db.CommitAll();
            response["status"] = "success";
            response["schedules"] = scheds;
        }
        catch (Exception ex)
        {
            response["status"] = "error";
            response["message"] = ex.Message;
        }
        return response;
    }

    private static Guid ParseGuid(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return Guid.Empty;
        if (uid.Length >= 36)
        {
            Guid g;
            if (Guid.TryParse(uid.Substring(0, 36), out g)) return g;
        }
        return Guid.Empty;
    }
}
