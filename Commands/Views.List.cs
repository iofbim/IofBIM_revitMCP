using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

public class ListViewsCommand : ICommand
{
    public Dictionary<string, object> Execute(UIApplication app, Dictionary<string, string> input)
    {
        var response = new Dictionary<string, object>();
        var doc = app.ActiveUIDocument?.Document;
        bool includeLinked = input.TryGetValue("include_linked", out var inc) && inc.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (doc == null)
        {
            response["status"] = "error";
            response["message"] = "No active document.";
            return response;
        }

        var result = new List<Dictionary<string, object>>();
        try
        {
            string conn = DbConfigHelper.GetConnectionString(input);
            BatchedPostgresDb db = null;
            if (!string.IsNullOrEmpty(conn))
            {
                db = new BatchedPostgresDb(conn);
            }

            DateTime lastSaved = System.IO.File.GetLastWriteTime(doc.PathName);

            if (ModelCache.TryGet(doc.PathName + "/views", lastSaved, out List<Dictionary<string, object>> cached))
            {
                response["status"] = "success";
                response["views"] = cached;
                return response;
            }

            if (db != null && db.GetModelLastSaved(doc.PathName) == lastSaved)
            {
                response["status"] = "up_to_date";
                return response;
            }

            var viewports = new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .GroupBy(v => v.ViewId.Value)
                .ToDictionary(g => g.Key, g => (long?)g.First().SheetId.Value);

            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate);

            foreach (var view in views)
            {
                var item = new Dictionary<string, object>();
                item["id"] = view.Id.Value;
                item["guid"] = view.UniqueId;
                item["name"] = view.Name;
                item["view_type"] = view.ViewType.ToString();
                item["scale"] = view.Scale;

                string discipline = view.Discipline.ToString();
                item["discipline"] = discipline;

                item["detail_level"] = view.DetailLevel.ToString();
                viewports.TryGetValue(view.Id.Value, out long? sheetId);
                item["associated_sheet_id"] = sheetId;
                item["doc_id"] = doc.PathName;
                result.Add(item);

                if (db != null)
                    db.UpsertView((int)view.Id.Value, Guid.Empty, view.Name, view.ViewType.ToString(), view.Scale, discipline, view.DetailLevel.ToString(), sheetId.HasValue ? (int?)sheetId.Value : null, doc.PathName, lastSaved);
            }

            if (db != null)
            {
                db.UpsertModelInfo(doc.PathName, doc.Title, ParseGuid(doc.ProjectInformation.UniqueId), lastSaved,
                    null, null);
                db.CommitAll();
            }

            // Optionally include views from loaded linked documents (read-only; no DB upsert to avoid collisions)
            if (includeLinked)
            {
                var links = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>();
                foreach (var link in links)
                {
                    Document linkDoc = null;
                    try { linkDoc = link.GetLinkDocument(); } catch { linkDoc = null; }
                    if (linkDoc == null) continue;

                    var linkViews = new FilteredElementCollector(linkDoc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate);

                    foreach (var v in linkViews)
                    {
                        var item = new Dictionary<string, object>();
                        item["id"] = v.Id.Value;
                        item["guid"] = v.UniqueId;
                        item["name"] = v.Name;
                        item["view_type"] = v.ViewType.ToString();
                        item["scale"] = v.Scale;
                        item["discipline"] = "Unknown";
                        item["detail_level"] = v.DetailLevel.ToString();
                        item["associated_sheet_id"] = null;
                        item["doc_id"] = linkDoc.PathName;
                        item["source"] = "link";
                        item["link_instance_id"] = link.Id.Value;
                        result.Add(item);
                    }
                }
            }

            ModelCache.Set(doc.PathName + "/views", lastSaved, result);
            response["status"] = "success";
            response["views"] = result;
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
