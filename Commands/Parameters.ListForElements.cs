// GetElementParametersCommand.cs - Retrieves parameters for elements and syncs them to PostgreSQL
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

public class ListElementParametersCommand : ICommand
{
    public Dictionary<string, object> Execute(UIApplication app, Dictionary<string, string> input)
    {
        var response = new Dictionary<string, object>();
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc?.Document;

        if (doc == null)
        {
            response["status"] = "error";
            response["message"] = "No active document.";
            return response;
        }

        string conn = DbConfigHelper.GetConnectionString(input);
        if (string.IsNullOrEmpty(conn))
        {
            response["status"] = "error";
            response["message"] = "No connection string found. " + DbConfigHelper.GetHelpMessage();
            return response;
        }

        var ids = ParseElementIds(input);
        if (ids.Count == 0)
        {
            response["status"] = "error";
            response["message"] = "No element_ids provided.";
            return response;
        }

        HashSet<string> filterNames = null;
        if (input.TryGetValue("param_names", out var namesStr) && !string.IsNullOrWhiteSpace(namesStr))
        {
            filterNames = new HashSet<string>(
                namesStr.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.OrdinalIgnoreCase);
        }

        DateTime lastSaved = System.IO.File.GetLastWriteTime(doc.PathName);
        var parameterCategories = ParameterMetadataCache.GetParameterCategories(doc, lastSaved);

        var syncedElements = new List<long>();
        var failedElements = new List<Dictionary<string, object>>();
        var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var db = new BatchedPostgresDb(conn))
        {
            foreach (var id in ids)
            {
                try
                {
                    var element = doc.GetElement(id);
                    if (element == null)
                    {
                        failedElements.Add(new Dictionary<string, object>
                        {
                            ["element_id"] = id.Value,
                            ["error"] = "Element not found."
                        });
                        continue;
                    }

                    StageElementMetadata(doc, element, db, lastSaved);
                    StageParameterMetadata(element, filterNames, parameterCategories, parameterNames, db, lastSaved, doc.PathName);

                    syncedElements.Add(element.Id.Value);
                }
                catch (Exception ex)
                {
                    failedElements.Add(new Dictionary<string, object>
                    {
                        ["element_id"] = id.Value,
                        ["error"] = ex.Message
                    });
                }
            }

            try
            {
                db.UpsertModelInfo(
                    doc.PathName,
                    doc.Title,
                    ParseGuid(doc.ProjectInformation?.UniqueId),
                    lastSaved,
                    null,
                    null);
                db.CommitAll();
            }
            catch (Exception ex)
            {
                failedElements.Add(new Dictionary<string, object>
                {
                    ["element_id"] = 0,
                    ["error"] = "Database commit failed: " + ex.Message
                });
            }
        }

        bool anyFailures = failedElements.Count > 0;
        response["status"] = !anyFailures ? "success" : (syncedElements.Count > 0 ? "partial" : "error");
        response["doc_id"] = doc.PathName;
        response["synced_element_ids"] = syncedElements;
        response["parameter_names"] = parameterNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (anyFailures)
            response["failed_elements"] = failedElements;

        if (!anyFailures)
        {
            response["message"] = $"Parameters synced for {syncedElements.Count} elements.";
        }
        else if (syncedElements.Count == 0)
        {
            response["message"] = "No element parameters were synced. Check failed_elements for details.";
        }
        else
        {
            response["message"] = "Some elements failed to sync. Check failed_elements for details.";
        }

        return response;
    }

    private static List<ElementId> ParseElementIds(Dictionary<string, string> input)
    {
        var ids = new List<ElementId>();
        if (input.TryGetValue("element_ids", out var idStr) && !string.IsNullOrWhiteSpace(idStr))
        {
            foreach (var part in idStr.Split(','))
            {
                if (long.TryParse(part.Trim(), out var intId))
                    ids.Add(new ElementId(intId));
            }
        }
        return ids;
    }

    private static void StageElementMetadata(Document doc, Element element, BatchedPostgresDb db, DateTime lastSaved)
    {
        string typeName = string.Empty;
        if (element != null)
        {
            var type = doc.GetElement(element.GetTypeId()) as ElementType;
            if (type != null)
                typeName = type.Name;
        }

        string levelName = string.Empty;
        if (element is Element el && el.LevelId != ElementId.InvalidElementId)
        {
            var level = doc.GetElement(el.LevelId);
            if (level != null)
                levelName = level.Name;
        }

        db.StageElement(
            (int)element.Id.Value,
            ParseGuid(element.UniqueId),
            element.Name,
            element.Category?.Name ?? string.Empty,
            typeName,
            levelName,
            doc.PathName,
            lastSaved);
    }

    private static void StageParameterMetadata(
        Element element,
        HashSet<string> filterNames,
        Dictionary<string, List<string>> parameterCategories,
        HashSet<string> parameterNames,
        BatchedPostgresDb db,
        DateTime lastSaved,
        string docPath)
    {
        foreach (Parameter param in element.Parameters)
        {
            if (param == null) continue;

            string name = param.Definition?.Name;
            if (string.IsNullOrEmpty(name)) continue;
            if (filterNames != null && !filterNames.Contains(name)) continue;

            parameterNames.Add(name);

            bool isType = param.Element != null && param.Element.Id != element.Id;
            string value = GetParameterValueString(param);

            string[] categories = null;
            if (parameterCategories != null && parameterCategories.TryGetValue(name, out var cats) && cats != null && cats.Count > 0)
                categories = cats.ToArray();

            db.StageParameter(
                (int)element.Id.Value,
                name,
                value,
                isType,
                categories,
                lastSaved,
                docPath);
        }
    }

    private static string GetParameterValueString(Parameter param)
    {
        switch (param.StorageType)
        {
            case StorageType.String:
                return param.AsString();
            case StorageType.Double:
                return param.AsDouble().ToString(CultureInfo.InvariantCulture);
            case StorageType.Integer:
                return param.AsInteger().ToString(CultureInfo.InvariantCulture);
            case StorageType.ElementId:
                var id = param.AsElementId();
                return id != null ? id.Value.ToString(CultureInfo.InvariantCulture) : null;
            default:
                return param.AsValueString();
        }
    }

    private static Guid ParseGuid(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return Guid.Empty;
        if (uid.Length >= 36 && Guid.TryParse(uid.Substring(0, 36), out var g))
            return g;
        return Guid.Empty;
    }
}
