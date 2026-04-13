using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RevitExtractor
{
    [Transaction(TransactionMode.Manual)]
    public class ExportModelToPostgresCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null)
            {
                message = "No active document.";
                return Result.Failed;
            }

            string dllDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string connPath = System.IO.Path.Combine(dllDir, "revit-conn.txt");

            if (!System.IO.File.Exists(connPath))
            {
                message = $"Connection file not found: {connPath}";
                return Result.Failed;
            }

            string conn = System.IO.File.ReadAllText(connPath).Trim();
            var db = new BatchedPostgresDb(conn);

            string modelPath = doc.PathName;
            DateTime lastSaved = System.IO.File.GetLastWriteTime(modelPath);

            try
            {
                var typeCollector = new FilteredElementCollector(doc).WhereElementIsElementType();
                var typeMap = new Dictionary<ElementId, string>();
                foreach (ElementType type in typeCollector)
                {
                    db.StageElementType((int)type.Id.Value, ParseGuid(type.UniqueId), type.FamilyName,
                        type.Name, type.Category?.Name ?? string.Empty, modelPath, lastSaved);

                    if (!typeMap.ContainsKey(type.Id))
                        typeMap[type.Id] = type.Name;
                }

                var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
                foreach (var element in collector)
                {
                    string typeName = typeMap.TryGetValue(element.GetTypeId(), out var tn) ? tn : string.Empty;
                    string levelName = string.Empty;

                    if (element.LevelId != ElementId.InvalidElementId)
                    {
                        var lvl = doc.GetElement(element.LevelId) as Level;
                        if (lvl != null) levelName = lvl.Name;
                    }

                    db.StageElement((int)element.Id.Value, ParseGuid(element.UniqueId), element.Name,
                        element.Category?.Name ?? string.Empty, typeName, levelName, modelPath, lastSaved);
                }

                db.CommitAll();

                foreach (ElementType type in typeCollector)
                {
                    foreach (Parameter param in type.Parameters)
                    {
                        if (param.Definition == null) continue;
                        string val = ParamToString(param);
                        db.StageTypeParameter((int)type.Id.Value, param.Definition.Name, val,
                            new[] { type.Category?.Name ?? string.Empty }, lastSaved, modelPath);
                    }
                }

                foreach (var element in collector)
                {
                    foreach (Parameter param in element.Parameters)
                    {
                        if (param.Definition == null) continue;
                        string val = ParamToString(param);
                        db.StageParameter((int)element.Id.Value, param.Definition.Name, val,
                            false,
                            new[] { element.Category?.Name ?? string.Empty }, lastSaved, modelPath);
                    }
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;

                string log = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR in ExportModelToPostgresCommand:\n{ex}\n";
                System.IO.File.AppendAllText("revit-export-error.log", log);

                return Result.Failed;
            }

            db.CommitAll();

            return Result.Succeeded;
        }

        private static string ParamToString(Parameter p)
        {
            try
            {
                if (p == null || p.Definition == null || p.StorageType == StorageType.None)
                    return null;

                switch (p.StorageType)
                {
                    case StorageType.String:
                        return SanitizeString(p.AsString());
                    case StorageType.Integer:
                        return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    case StorageType.Double:
                        return p.AsDouble().ToString(CultureInfo.InvariantCulture);
                    case StorageType.ElementId:
                        return p.AsElementId().Value.ToString(CultureInfo.InvariantCulture);
                    default:
                        return null;
                }
            }
            catch
            {
                // Unexpected conversion failure
                return null;
            }
        }

        private static string SanitizeString(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Replace("\0", string.Empty);
        }

        private static Guid ParseGuid(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return Guid.Empty;
            if (uid.Length >= 36 && Guid.TryParse(uid.Substring(0, 36), out var g))
                return g;
            return Guid.Empty;
        }
    }
}
