// GetFamiliesAndTypesCommand.cs - lists all families and their types in the model
// Extended to include family GUID and document id
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

public class ListFamiliesAndTypesCommand : ICommand
{
    public Dictionary<string, object> Execute(UIApplication app, Dictionary<string, string> input)
    {
        var doc = app.ActiveUIDocument.Document;
        var response = new Dictionary<string, object>();
        var result = new List<Dictionary<string, object>>();
        bool includeLinked = input.TryGetValue("include_linked", out var inc) && inc.Equals("true", StringComparison.OrdinalIgnoreCase);

        string conn = DbConfigHelper.GetConnectionString(input);
        BatchedPostgresDb db = null;
        if (!string.IsNullOrEmpty(conn))
        {
            db = new BatchedPostgresDb(conn);
        }

        DateTime lastSaved = System.IO.File.GetLastWriteTime(doc.PathName);
        if (db != null && db.GetModelLastSaved(doc.PathName) == lastSaved)
        {
            response["status"] = "up_to_date";
            return response;
        }

        try
        {
            Type filterType = typeof(ElementType);

            if (input.ContainsKey("class_name"))
            {
                string className = input["class_name"];
                var revitApiAssembly = typeof(FamilyInstance).Assembly;
                var resolved = revitApiAssembly.GetTypes().FirstOrDefault(t => t.Name == className);

                if (resolved != null && typeof(ElementType).IsAssignableFrom(resolved))
                {
                    filterType = resolved;
                }
                else
                {
                    response["status"] = "error";
                    response["message"] = $"'{className}' is not a valid Revit ElementType class.";
                    return response;
                }
            }

            string cacheKey = doc.PathName + "/families-" + filterType.Name + (includeLinked ? "-withlinks" : "");
            if (ModelCache.TryGet(cacheKey, lastSaved, out List<Dictionary<string, object>> cached))
            {
                response["status"] = "success";
                response["types"] = cached;
                return response;
            }

            var types = new FilteredElementCollector(doc)
                .WhereElementIsElementType()
                .OfClass(filterType)
                .Cast<ElementType>();

            foreach (var type in types)
            {
                if (type.FamilyName != null && type.Name != null)
                {
                    var item = new Dictionary<string, object>();
                    item["family"] = type.FamilyName;
                    item["type"] = type.Name;
                    item["id"] = type.Id.Value.ToString();
                    item["category"] = type.Category?.Name ?? string.Empty;
                    item["guid"] = type.UniqueId;
                    item["doc_id"] = doc.PathName;
                    result.Add(item);

                    if (db != null)
                    {
                        db.UpsertFamily(type.FamilyName, type.Name, type.Category?.Name ?? string.Empty, type.UniqueId, doc.PathName, lastSaved);
                    }
                }
            }

            // Optionally include linked documents (read-only; no DB upsert to avoid collisions)
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

                    var linkTypes = new FilteredElementCollector(linkDoc)
                        .WhereElementIsElementType()
                        .OfClass(filterType)
                        .Cast<ElementType>();

                    foreach (var type in linkTypes)
                    {
                        if (type.FamilyName != null && type.Name != null)
                        {
                            var item = new Dictionary<string, object>();
                            item["family"] = type.FamilyName;
                            item["type"] = type.Name;
                            item["id"] = type.Id.Value.ToString();
                            item["category"] = type.Category?.Name ?? string.Empty;
                            item["guid"] = type.UniqueId;
                            item["doc_id"] = linkDoc.PathName;
                            item["source"] = "link";
                            item["link_instance_id"] = link.Id.Value;
                            result.Add(item);
                        }
                    }
                }
            }

            if (db != null)
            {
                db.UpsertModelInfo(doc.PathName, doc.Title, ParseGuid(doc.ProjectInformation.UniqueId), lastSaved,
                    null, null);
                db.CommitAll();
            }

            ModelCache.Set(cacheKey, lastSaved, result);
            response["status"] = "success";
            response["types"] = result;
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
