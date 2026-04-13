// ListElementsCommand.cs - example of a typed commandset handler
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

public class ListElementsCommand : ICommand
{
    public Dictionary<string, object> Execute(UIApplication app, Dictionary<string, string> input)
    {
        var doc = app.ActiveUIDocument.Document;
        var response = new Dictionary<string, object>();
        string categoryName = input.ContainsKey("category") ? input["category"] : "Walls";
        bool includeLinked = input.TryGetValue("include_linked", out var inc) && inc.Equals("true", StringComparison.OrdinalIgnoreCase);

        string conn = DbConfigHelper.GetConnectionString(input);
        BatchedPostgresDb db = null;
        if (!string.IsNullOrEmpty(conn))
        {
            db = new BatchedPostgresDb(conn);
        }

        DateTime lastSaved = System.IO.File.GetLastWriteTime(doc.PathName);

        string cacheKey = doc.PathName + "/elements-" + categoryName + (includeLinked ? "-withlinks" : "");
        if (ModelCache.TryGet(cacheKey, lastSaved,
                out List<Dictionary<string, object>> cached))
        {
            response["status"] = "success";
            response["elements"] = cached;
            return response;
        }

        var collector = new FilteredElementCollector(doc)
            .OfCategory(CategoryUtils.ParseBuiltInCategory(categoryName))
            .WhereElementIsNotElementType();

        var elements = new List<Dictionary<string, object>>();
        foreach (var e in collector)
        {
            var item = new Dictionary<string, object>();
            item["id"] = e.Id.Value;
            item["name"] = e.Name;
            item["doc_id"] = doc.PathName;
            elements.Add(item);

            if (db != null)
            {
                string typeName = string.Empty;
                var et = doc.GetElement(e.GetTypeId()) as ElementType;
                if (et != null) typeName = et.Name;

                string levelName = string.Empty;
                if (e is Element el && el.LevelId != ElementId.InvalidElementId)
                {
                    var lvl = doc.GetElement(el.LevelId);
                    if (lvl != null) levelName = lvl.Name;
                }

                db.StageElement((int)e.Id.Value, ParseGuid(e.UniqueId), e.Name,
                    e.Category?.Name ?? string.Empty, typeName, levelName, doc.PathName, lastSaved);
            }
        }

        // Optionally include loaded linked documents (read-only). We do NOT upsert linked elements to DB to avoid collisions
        // across multiple link instances and documents in current schema.
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

                var linked = new FilteredElementCollector(linkDoc)
                    .OfCategory(CategoryUtils.ParseBuiltInCategory(categoryName))
                    .WhereElementIsNotElementType();

                foreach (var e in linked)
                {
                    var item = new Dictionary<string, object>();
                    item["id"] = e.Id.Value;
                    item["name"] = e.Name;
                    item["doc_id"] = linkDoc.PathName;
                    item["source"] = "link";
                    item["link_instance_id"] = link.Id.Value;
                    elements.Add(item);
                }
            }
        }

        if (db != null)
        {
            db.UpsertModelInfo(doc.PathName, doc.Title, ParseGuid(doc.ProjectInformation.UniqueId), lastSaved,
                null, null);
            db.CommitAll();
        }

        ModelCache.Set(cacheKey, lastSaved, elements);
        response["status"] = "success";
        response["elements"] = elements;
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
