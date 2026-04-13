// GetModelContextCommand.cs - retrieves model metadata and project parameters
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public class ListModelContextCommand : ICommand
{
    public Dictionary<string, object> Execute(UIApplication app, Dictionary<string, string> input)
    {
        var response = new Dictionary<string, object>();
        var doc = app?.ActiveUIDocument?.Document;

        if (doc == null)
        {
            response["status"] = "error";
            response["message"] = "No active Revit document.";
            return response;
        }

        try
        {
            string conn = DbConfigHelper.GetConnectionString(input);
            PostgresDb db = null;
            if (!string.IsNullOrEmpty(conn))
                db = new BatchedPostgresDb(conn);
            DateTime lastSaved = System.IO.File.GetLastWriteTime(doc.PathName);

            // project information parameters
            var projectInfo = doc.ProjectInformation;
            var info = new Dictionary<string, string>();
            foreach (Parameter p in projectInfo.Parameters)
            {
                if (p == null || string.IsNullOrEmpty(p.Definition?.Name)) continue;
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
                }
                info[p.Definition.Name] = val;
            }

            // project parameters
            var bindingMap = doc.ParameterBindings;
            var iterator = bindingMap.ForwardIterator();
            var parameters = new List<Dictionary<string, object>>();

            iterator.Reset();
            while (iterator.MoveNext())
            {
                Definition definition = iterator.Key;
                ElementBinding binding = iterator.Current as ElementBinding;

                if (definition == null || binding == null) continue;

                var paramData = new Dictionary<string, object>();
                paramData["name"] = definition.Name;
                paramData["parameter_type"] = LabelUtils.GetLabelForGroup(definition.GetGroupTypeId());
                paramData["unit_type_id"] = definition.GetDataType()?.TypeId?.ToString() ?? string.Empty;
                paramData["binding_type"] = binding is InstanceBinding ? "Instance" : "Type";

                var categories = new List<string>();
                foreach (Category cat in binding.Categories)
                {
                    if (cat != null)
                        categories.Add(cat.Name);
                }
                paramData["categories"] = categories;

                parameters.Add(paramData);
            }

            response["status"] = "success";
            response["model_name"] = doc.Title;
            response["guid"] = ParseGuid(doc.ProjectInformation.UniqueId).ToString();
            response["last_saved"] = lastSaved.ToString("yyyy-MM-ddTHH:mm:ss");
            response["project_info"] = info;
            response["project_parameters"] = parameters;

            if (db != null)
            {
                string jsonInfo = JsonSerializer.Serialize(info);
                string jsonParams = JsonSerializer.Serialize(parameters);
                db.UpsertModelInfo(doc.PathName, doc.Title, ParseGuid(doc.ProjectInformation.UniqueId), lastSaved, jsonInfo, jsonParams);
            }
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
