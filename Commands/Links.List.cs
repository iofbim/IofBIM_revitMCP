using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

public class ListLinkedDocumentsCommand : ICommand
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

        var result = new List<Dictionary<string, object>>();

        try
        {
            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var inst in instances)
            {
                var item = new Dictionary<string, object>();
                item["link_instance_id"] = inst.Id.Value;

                // Try to access linked document (may be null if unloaded)
                Document linkDoc = null;
                try { linkDoc = inst.GetLinkDocument(); } catch { linkDoc = null; }
                bool isLoaded = linkDoc != null;
                item["loaded"] = isLoaded;

                // Link type and path
                var linkType = doc.GetElement(inst.GetTypeId()) as RevitLinkType;
                string linkPath = null;
                try
                {
                    var ext = linkType?.GetExternalFileReference();
                    if (ext != null)
                    {
                        var mp = ext.GetPath();
                        if (mp != null)
                            linkPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
                    }
                }
                catch { }

                item["link_doc_path"] = linkPath ?? string.Empty;
                item["link_doc_title"] = isLoaded ? linkDoc.Title : (linkType?.Name ?? string.Empty);

                // Linked document GUID (via ProjectInformation.UniqueId)
                Guid linkGuid = Guid.Empty;
                if (isLoaded)
                {
                    try { linkGuid = ParseGuid(linkDoc.ProjectInformation?.UniqueId); } catch { linkGuid = Guid.Empty; }
                }
                item["link_doc_guid"] = linkGuid.ToString();

                // Instance total transform to host
                try
                {
                    Transform t = inst.GetTotalTransform();
                    item["transform_origin"] = new Dictionary<string, object>
                    {
                        {"x", t.Origin.X },
                        {"y", t.Origin.Y },
                        {"z", t.Origin.Z }
                    };
                    item["transform_basisX"] = new double[] { t.BasisX.X, t.BasisX.Y, t.BasisX.Z };
                    item["transform_basisY"] = new double[] { t.BasisY.X, t.BasisY.Y, t.BasisY.Z };
                    item["transform_basisZ"] = new double[] { t.BasisZ.X, t.BasisZ.Y, t.BasisZ.Z };

                    // Approximate rotation about Z from BasisX
                    double rotZ = Math.Atan2(t.BasisX.Y, t.BasisX.X);
                    item["rotation_z_radians"] = rotZ;
                }
                catch { }

                // Angle to true north from the linked document (if loaded)
                if (isLoaded)
                {
                    try
                    {
                        var loc = linkDoc.ActiveProjectLocation;
                        var pp = loc?.GetProjectPosition(XYZ.Zero);
                        if (pp != null)
                        {
                            item["angle_to_true_north_radians"] = pp.Angle;
                        }
                    }
                    catch { }
                }

                result.Add(item);
            }

            response["status"] = "success";
            response["links"] = result;
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
