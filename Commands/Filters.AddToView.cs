// AddViewFilterCommand.cs - Creates a view filter for a category based on parameter rule
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

public class AddViewFilterCommand : ICommand
{
    public Dictionary<string, object> Execute(UIApplication app, Dictionary<string, string> input)
    {
        var doc = app.ActiveUIDocument.Document;
        var uidoc = app.ActiveUIDocument;
        var view = doc.ActiveView;

        var response = new Dictionary<string, object>();

        try
        {
            if (!input.TryGetValue("category", out var categoryName) ||
                !input.TryGetValue("filter_name", out var filterName) ||
                !input.TryGetValue("parameter", out var parameterName) ||
                !input.TryGetValue("value", out var value))
            {
                response["status"] = "error";
                response["message"] = "Required fields: category, filter_name, parameter, value.";
                return response;
            }

            var builtInCategory = CategoryUtils.ParseBuiltInCategory(categoryName);
            var category = Category.GetCategory(doc, builtInCategory);
            if (category == null)
                throw new Exception("Invalid category.");

            var sampleElement = new FilteredElementCollector(doc)
                .OfCategory(builtInCategory)
                .WhereElementIsNotElementType()
                .FirstOrDefault();
            if (sampleElement == null)
                throw new Exception("No element found in the specified category.");

            var param = sampleElement.Parameters
                .Cast<Parameter>()
                .FirstOrDefault(p => p.Definition?.Name == parameterName);
            if (param == null)
                throw new Exception($"Parameter '{parameterName}' not found.");

            var definition = param.Definition as InternalDefinition;
            if (definition == null)
                throw new Exception("Only internal (built-in) parameters are supported.");

            var paramId = definition.Id;
            FilterRule rule = null;

            switch (param.StorageType)
            {
                case StorageType.String:
                    rule = ParameterFilterRuleFactory.CreateEqualsRule(paramId, value as string);
                    break;
                case StorageType.Integer:
                    if (value.Equals("Yes", StringComparison.OrdinalIgnoreCase)) value = "1";
                    if (value.Equals("No", StringComparison.OrdinalIgnoreCase)) value = "0";
                    if (!int.TryParse(value, out var intVal))
                        throw new Exception("Value must be an integer or 'Yes'/'No'.");
                    rule = ParameterFilterRuleFactory.CreateEqualsRule(paramId, intVal);
                    break;
                case StorageType.Double:
                    if (!double.TryParse(value, out var dblVal))
                        throw new Exception("Value must be a number.");
                    rule = ParameterFilterRuleFactory.CreateEqualsRule(paramId, dblVal, 0.0001);
                    break;
                default:
                    throw new Exception("Unsupported parameter type.");
            }

            ElementFilter filterRule = new ElementParameterFilter(new List<FilterRule> { rule });

            using (var tx = new Transaction(doc, "Add View Filter"))
            {
                tx.Start();

                var filter = ParameterFilterElement.Create(doc, filterName, new List<ElementId> { category.Id }, filterRule);
                view.AddFilter(filter.Id);

                bool visibility = true;
                if (input.TryGetValue("visible", out var visStr))
                    visibility = visStr.Equals("true", StringComparison.OrdinalIgnoreCase);
                view.SetFilterVisibility(filter.Id, visibility);

                var overrideGraphicSettings = new OverrideGraphicSettings();

                if (input.TryGetValue("color", out var colorStr))
                {
                    var parts = colorStr.Split(',').Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).ToArray();
                    if (parts.Length == 3)
                    {
                        overrideGraphicSettings.SetProjectionLineColor(new Color((byte)parts[0], (byte)parts[1], (byte)parts[2]));
                    }
                }

                if (input.TryGetValue("line_pattern", out var patternName))
                {
                    var patternElem = new FilteredElementCollector(doc)
                        .OfClass(typeof(LinePatternElement))
                        .FirstOrDefault(e => e.Name.Equals(patternName, StringComparison.OrdinalIgnoreCase)) as LinePatternElement;
                    if (patternElem != null)
                    {
                        overrideGraphicSettings.SetProjectionLinePatternId(patternElem.Id);
                    }
                }

                // Fill overrides
                if (input.TryGetValue("fill_color", out var fillColorStr))
                {
                    var fillParts = fillColorStr.Split(',').Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).ToArray();
                    if (fillParts.Length == 3)
                    {
                        overrideGraphicSettings.SetSurfaceForegroundPatternColor(new Color((byte)fillParts[0], (byte)fillParts[1], (byte)fillParts[2]));
                    }
                }

                if (input.TryGetValue("fill_pattern", out var fillPatternName))
                {
                    var fillPatternElem = new FilteredElementCollector(doc)
                        .OfClass(typeof(FillPatternElement))
                        .FirstOrDefault(e => e.Name.Equals(fillPatternName, StringComparison.OrdinalIgnoreCase)) as FillPatternElement;
                    if (fillPatternElem != null)
                    {
                        overrideGraphicSettings.SetSurfaceForegroundPatternId(fillPatternElem.Id);
                    }
                }

                // Optional: line weight override
                if (input.TryGetValue("line_weight", out var lineWeightStr) && int.TryParse(lineWeightStr, out var lineWeight))
                {
                    overrideGraphicSettings.SetProjectionLineWeight(lineWeight);
                }

                // Optional: cut overrides
                if (input.TryGetValue("cut_color", out var cutColorStr))
                {
                    var cutParts = cutColorStr.Split(',').Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).ToArray();
                    if (cutParts.Length == 3)
                    {
                        overrideGraphicSettings.SetCutLineColor(new Color((byte)cutParts[0], (byte)cutParts[1], (byte)cutParts[2]));
                    }
                }

                if (input.TryGetValue("cut_fill_color", out var cutFillStr))
                {
                    var fillParts = cutFillStr.Split(',').Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).ToArray();
                    if (fillParts.Length == 3)
                    {
                        overrideGraphicSettings.SetCutForegroundPatternColor(new Color((byte)fillParts[0], (byte)fillParts[1], (byte)fillParts[2]));
                    }
                }

                if (input.TryGetValue("cut_fill_pattern", out var cutFillPatternName))
                {
                    var cutPatternElem = new FilteredElementCollector(doc)
                        .OfClass(typeof(FillPatternElement))
                        .FirstOrDefault(e => e.Name.Equals(cutFillPatternName, StringComparison.OrdinalIgnoreCase)) as FillPatternElement;
                    if (cutPatternElem != null)
                    {
                        overrideGraphicSettings.SetCutForegroundPatternId(cutPatternElem.Id);
                    }
                }

                if (input.TryGetValue("cut_line_pattern", out var cutLinePatternName))
                {
                    var cutPatternElem = new FilteredElementCollector(doc)
                        .OfClass(typeof(LinePatternElement))
                        .FirstOrDefault(e => e.Name.Equals(cutLinePatternName, StringComparison.OrdinalIgnoreCase)) as LinePatternElement;
                    if (cutPatternElem != null)
                    {
                        overrideGraphicSettings.SetCutLinePatternId(cutPatternElem.Id);
                    }
                }

                if (input.TryGetValue("cut_line_weight", out var cutWeightStr) && int.TryParse(cutWeightStr, out var cutWeight))
                {
                    overrideGraphicSettings.SetCutLineWeight(cutWeight);
                }

                view.SetFilterOverrides(filter.Id, overrideGraphicSettings);

                response["status"] = "success";
                response["filterId"] = filter.Id.Value;

                tx.Commit();
            }
        }
        catch (Exception ex)
        {
            response["status"] = "error";
            response["message"] = ex.Message;
        }

        return response;
    }
}
