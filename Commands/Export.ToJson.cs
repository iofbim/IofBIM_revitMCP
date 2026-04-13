// ExportToJsonCommand.cs - Exports elements and their parameters to JSON
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

public class ExportToJsonCommand : ICommand
{
    public Dictionary<string, object> Execute(UIApplication app, Dictionary<string, string> input)
    {
        var doc = app.ActiveUIDocument.Document;
        var response = new Dictionary<string, object>();
        var result = new List<Dictionary<string, object>>();

        try
        {
            List<string> categoriesToExport = new List<string>();
            if (input.ContainsKey("categories"))
            {
                categoriesToExport = input["categories"].Split(',').Select(s => s.Trim()).ToList();
            }
            else
            {
                // Default category if none provided
                categoriesToExport.Add("Walls");
            }

            foreach (var categoryName in categoriesToExport)
            {
                BuiltInCategory bic = (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), categoryName, true);
                var elements = new FilteredElementCollector(doc)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType()
                    .ToElements();

                foreach (var element in elements)
                {
                    var elementData = new Dictionary<string, object>
                    {
                        ["id"] = element.Id.Value,
                        ["name"] = element.Name,
                        ["category"] = categoryName
                    };

                    var paramDict = new Dictionary<string, string>();
                    foreach (Parameter param in element.Parameters)
                    {
                        try
                        {
                            string name = param.Definition?.Name ?? "(no name)";
                            string value = param.AsValueString() ?? param.AsString() ?? param.AsInteger().ToString();
                            paramDict[name] = value;
                        }
                        catch { }
                    }

                    elementData["parameters"] = paramDict;
                    result.Add(elementData);
                }
            }

            response["status"] = "success";
            response["elements"] = result;
        }
        catch (Exception ex)
        {
            response["status"] = "error";
            response["message"] = ex.Message;
        }

        return response;
    }
}
