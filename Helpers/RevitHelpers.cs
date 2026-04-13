// RevitHelpers.cs - Functional utilities for IronPython scripts
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

public static class RevitHelpers
{
    public static List<Element> GetElementsByCategory(Document doc, string categoryName)
    {
        var bic = CategoryUtils.ParseBuiltInCategory(categoryName);
        return new FilteredElementCollector(doc)
            .OfCategory(bic)
            .WhereElementIsNotElementType()
            .ToElements()
            .ToList();
    }

    public static void SetParameter(Element element, string paramName, string value)
    {
        var param = element.LookupParameter(paramName);
        if (param != null && !param.IsReadOnly)
        {
            param.Set(value);
        }
    }

    public static Dictionary<string, object> CaptureToolState(UIDocument uidoc)
    {
        var doc = uidoc?.Document;
        if (doc == null) return null;

        var state = new Dictionary<string, object>();
        state["document_name"] = doc.Title;

        var view = doc.ActiveView;
        state["active_view"] = new Dictionary<string, object>
        {
            ["name"] = view.Name,
            ["id"] = view.Id.Value,
            ["type"] = view.ViewType.ToString()
        };

        var selected = new List<Dictionary<string, object>>();
        foreach (var id in uidoc.Selection.GetElementIds())
        {
            var el = doc.GetElement(id);
            if (el == null) continue;

            var item = new Dictionary<string, object>
            {
                ["id"] = el.Id.Value,
                ["category"] = el.Category?.Name ?? string.Empty,
                ["type_name"] = doc.GetElement(el.GetTypeId())?.Name ?? string.Empty
            };

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
        return state;
    }
}
