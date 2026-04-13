using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

public class ModifyElementsCommand : ICommand
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

        if (!input.TryGetValue("changes", out var changesStr))
        {
            response["status"] = "error";
            response["message"] = "Missing 'changes' JSON array.";
            return response;
        }

        JArray changes;
        try
        {
            changes = JArray.Parse(changesStr);
        }
        catch
        {
            response["status"] = "error";
            response["message"] = "Invalid 'changes' format. Must be a JSON array.";
            return response;
        }

        var results = new List<Dictionary<string, object>>();

        using (var tx = new Transaction(doc, "Modify Elements"))
        {
            tx.Start();

            foreach (JObject change in changes.OfType<JObject>())
            {
                var item = new Dictionary<string, object>();
                long idInt = change.TryGetValue("element_id", out var idToken) && long.TryParse(idToken.ToString(), out var tmp)
                    ? tmp : -1;

                if (idInt < 0)
                {
                    item["status"] = "invalid_id";
                    results.Add(item);
                    continue;
                }

                item["element_id"] = idInt;
                var elementId = new ElementId(idInt);
                var element = doc.GetElement(elementId);

                if (element == null)
                {
                    item["status"] = "not_found";
                    results.Add(item);
                    continue;
                }

                bool typeChanged = false;
                if (change.TryGetValue("new_type_name", out var typeToken))
                {
                    string typeName = typeToken.ToString();
                    var newType = new FilteredElementCollector(doc)
                        .OfClass(typeof(ElementType))
                        .Cast<ElementType>()
                        .FirstOrDefault(t => t.Name == typeName && t.FamilyName != null);

                    if (newType != null)
                    {
                        try
                        {
                            element.ChangeTypeId(newType.Id);
                            typeChanged = true;
                        }
                        catch
                        {
                            item["type_error"] = $"Failed to change type to '{typeName}'";
                        }
                    }
                    else
                    {
                        item["type_error"] = $"Type '{typeName}' not found";
                    }
                }

                var updatedParams = new List<string>();
                var skippedParams = new List<string>();
                if (change.TryGetValue("parameters", out var paramToken) && paramToken.Type == JTokenType.Object)
                {
                    var paramObj = (JObject)paramToken;
                    foreach (var pair in paramObj)
                    {
                        var param = element.LookupParameter(pair.Key);
                        if (param == null || param.IsReadOnly)
                        {
                            skippedParams.Add(pair.Key);
                            continue;
                        }

                        bool success = false;
                        string value = pair.Value?.ToString();
                        switch (param.StorageType)
                        {
                            case StorageType.String:
                                success = param.Set(value);
                                break;
                            case StorageType.Integer:
                                if (int.TryParse(value, out var intVal))
                                    success = param.Set(intVal);
                                break;
                            case StorageType.Double:
                                if (double.TryParse(value, out var dblVal))
                                    success = param.Set(dblVal);
                                break;
                            case StorageType.ElementId:
                                if (long.TryParse(value, out var elid))
                                    success = param.Set(new ElementId(elid));
                                break;
                        }
                        if (success)
                            updatedParams.Add(pair.Key);
                        else
                            skippedParams.Add(pair.Key);
                    }
                }

                if (typeChanged)
                    item["type_changed"] = true;
                if (updatedParams.Count > 0)
                    item["updated_parameters"] = updatedParams;
                if (skippedParams.Count > 0)
                    item["skipped_parameters"] = skippedParams;

                item["status"] = "success";
                results.Add(item);
            }

            tx.Commit();
        }

        response["status"] = "success";
        response["results"] = results;
        return response;
    }
}
