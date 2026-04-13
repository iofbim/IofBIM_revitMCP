using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;

public class CaptureToolStateCommand : ICommand
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

        try
        {
            var state = new Dictionary<string, object>();
            state["document_name"] = doc.Title;

            var view = doc.ActiveView;
            var activeView = new Dictionary<string, object>
            {
                ["name"] = view.Name,
                ["id"] = view.Id.Value,
                ["type"] = view.ViewType.ToString()
            };
            state["active_view"] = activeView;

            var selected = new List<Dictionary<string, object>>();
            foreach (ElementId id in uidoc.Selection.GetElementIds())
            {
                var el = doc.GetElement(id);
                if (el == null) continue;

                var item = new Dictionary<string, object>();
                item["id"] = el.Id.Value;
                item["category"] = el.Category?.Name ?? string.Empty;
                item["type_name"] = doc.GetElement(el.GetTypeId())?.Name ?? string.Empty;

                var paramDict = new Dictionary<string, string>();
                foreach (Parameter p in el.Parameters)
                {
                    if (p.Definition?.Name == null) continue;

                    string val = string.Empty;
                    switch (p.StorageType)
                    {
                        case StorageType.String:
                            val = p.AsString();
                            break;
                        case StorageType.Integer:
                            val = p.AsInteger().ToString();
                            break;
                        case StorageType.Double:
                            val = p.AsDouble().ToString();
                            break;
                        case StorageType.ElementId:
                            val = p.AsElementId().Value.ToString();
                            break;
                        default:
                            val = string.Empty;
                            break;
                    }

                    paramDict[p.Definition.Name] = val ?? string.Empty;
                }
                item["parameters"] = paramDict;

                selected.Add(item);
            }
            state["selected_elements"] = selected;

            response["status"] = "success";
            response["tool_state"] = state;
        }
        catch (Exception ex)
        {
            response["status"] = "error";
            response["message"] = ex.Message;
        }

        return response;
    }
}
