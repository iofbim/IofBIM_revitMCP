// FilterByParameterCommand.cs - Filters elements by parameter value
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

public class FilterByParameterCommand : ICommand
{
    public Dictionary<string, object> Execute(UIApplication app, Dictionary<string, string> input)
    {
        var response = new Dictionary<string, object>();
        var doc = app.ActiveUIDocument.Document;
        var result = new List<Dictionary<string, object>>();

        try
        {
            if (!input.ContainsKey("param") || !input.ContainsKey("value"))
            {
                response["status"] = "error";
                response["message"] = "Missing 'param' or 'value' field.";
                return response;
            }

            string targetParam = input["param"];
            string targetValue = input["value"];

            List<Element> elements = null;
            if (input.ContainsKey("input_elements"))
            {
                var elementList = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(input["input_elements"]);
                elements = elementList
                    .Select(dict => dict.ContainsKey("Id")
                        ? doc.GetElement(new ElementId(Convert.ToInt64(dict["Id"])))
                        : null)
                    .Where(e => e != null)
                    .ToList();
            }
            else
            {
                response["status"] = "error";
                response["message"] = "No elements to filter. Provide 'input_elements'.";
                return response;
            }

            // Collect filtered elements
            foreach (var e in elements)
            {
                var param = e.LookupParameter(targetParam);
                if (param == null) continue;

                string value = param.AsValueString() ?? param.AsString() ?? param.AsInteger().ToString();

                if (value != null && value.Equals(targetValue, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new Dictionary<string, object>
                    {
                        { "Id", e.Id.Value },
                        { "Name", e.Name }
                    });
                }
            }

            // Produce summary aggregates for UI
            var filteredElements = elements
                .Where(e => result.Any(r => (long)r["Id"] == e.Id.Value))
                .ToList();

            int total = filteredElements.Count;
            var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            double sumArea = 0, sumVolume = 0, sumLength = 0;

            foreach (var e in filteredElements)
            {
                try
                {
                    var typeElem = doc.GetElement(e.GetTypeId()) as ElementType;
                    var typeName = typeElem?.Name ?? "(Type)";
                    if (!byType.ContainsKey(typeName)) byType[typeName] = 0;
                    byType[typeName]++;

                    double v;
                    if (TryGetDouble(e, BuiltInParameter.HOST_AREA_COMPUTED, out v) || TryGetDoubleByName(e, "Area", out v)) sumArea += v;
                    if (TryGetDouble(e, BuiltInParameter.HOST_VOLUME_COMPUTED, out v) || TryGetDoubleByName(e, "Volume", out v)) sumVolume += v;
                    if (TryGetDouble(e, BuiltInParameter.CURVE_ELEM_LENGTH, out v) || TryGetDoubleByName(e, "Length", out v)) sumLength += v;
                }
                catch { }
            }

            var byTypeArr = byType.Select(kv => new Dictionary<string, object>{{"type", kv.Key},{"count", kv.Value}}).ToList();
            var summary = new Dictionary<string, object>
            {
                { "total", total },
                { "by_type", byTypeArr },
                { "sums", new Dictionary<string, object>{ {"area", sumArea}, {"volume", sumVolume}, {"length", sumLength}, {"count", total} } },
                { "param", targetParam },
                { "value", targetValue }
            };

            response["status"] = "success";
            response["elements"] = result;
            response["summary"] = summary;

            // Optionally push UI event to DB if connection available
            try
            {
                string conn = DbConfigHelper.GetConnectionString(input);
                if (!string.IsNullOrEmpty(conn))
                {
                    var db = new PostgresDb(conn);
                    var sql = "INSERT INTO ui_events (session_id, doc_id, event_type, payload) VALUES (@s, @d, @t, @p)";
                    var session = input.ContainsKey("session_id") ? (object)input["session_id"] : DBNull.Value;
                    var payload = JsonConvert.SerializeObject(new Dictionary<string, object>{
                        {"kind","filter_summary"},
                        {"summary", summary}
                    });
                    var args = new []{
                        new Npgsql.NpgsqlParameter("@s", session),
                        new Npgsql.NpgsqlParameter("@d", (object)doc?.PathName ?? DBNull.Value),
                        new Npgsql.NpgsqlParameter("@t", "filter_summary"),
                        new Npgsql.NpgsqlParameter("@p", NpgsqlTypes.NpgsqlDbType.Jsonb){ Value = payload }
                    };
                    db.ExecuteNonQuery(sql, args);
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            response["status"] = "error";
            response["message"] = ex.Message;
        }

        return response;
    }

    private static bool TryGetDouble(Element e, BuiltInParameter bip, out double val)
    {
        val = 0;
        try
        {
            var p = e.get_Parameter(bip);
            if (p != null && p.StorageType == StorageType.Double)
            {
                val = p.AsDouble();
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool TryGetDoubleByName(Element e, string name, out double val)
    {
        val = 0;
        try
        {
            var p = e.LookupParameter(name);
            if (p != null && p.StorageType == StorageType.Double)
            {
                val = p.AsDouble();
                return true;
            }
        }
        catch { }
        return false;
    }
}
