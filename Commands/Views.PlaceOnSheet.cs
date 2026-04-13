// PlaceViewsOnSheet.cs – Places selected views on a sheet starting bottom‑right, stacking upward with column wrap.
// Features:
//   • Optional "offsetRight" (mm, default 120)
//   • Partial‑success: already‑placed viewports remain if later placement fails
//   • Enhanced error reporting: ALL error/partial responses include a comma‑separated
//     list `remaining_view_ids` indicating the IDs that were *not* placed
//
// Returns
//   status   = "success" | "partial" | "error"
//   placed   = count of successfully placed views (omitted on early fatal errors)
//   unplaced = count remaining (omitted on early fatal errors)
//   remaining_view_ids = "id1,id2,…" if any remain

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

public class PlaceViewsOnSheetCommand : ICommand
{
    public Dictionary<string, object> Execute(UIApplication app, Dictionary<string, string> input)
    {
        var response = new Dictionary<string, object>();
        var uidoc = app?.ActiveUIDocument;
        var doc = uidoc?.Document;

        // Helper for early‑exit errors (before we begin placing anything)
        void EarlyError(string msg, IEnumerable<string> remaining = null)
        {
            response["status"] = "error";
            response["message"] = msg;
            if (remaining != null)
                response["remaining_view_ids"] = string.Join(",", remaining);
        }

        if (doc == null)
        {
            EarlyError("No active Revit document.");
            return response;
        }

        // ---------- Parse & validate input ----------
        if (!input.TryGetValue("view_ids", out var viewsStr) ||
            !input.TryGetValue("sheet_id", out var sheetStr) ||
            !long.TryParse(sheetStr, out var sheetIdInt))
        {
            EarlyError("Missing or invalid 'view_ids' or 'sheet_id'.");
            return response;
        }

        // Optional right‑margin (mm)
        double offsetRightMm = 120;
        if (input.TryGetValue("offsetRight", out var offStr) &&
            double.TryParse(offStr, out var parsedMm) && parsedMm > 0)
        {
            offsetRightMm = parsedMm;
        }

        // View ids – keep integer list for reporting
        var viewIdInts = viewsStr.Split(',')
                                 .Select(s => s.Trim())
                                 .Where(s => long.TryParse(s, out _))
                                 .Select(long.Parse)
                                 .ToList();
        if (viewIdInts.Count == 0)
        {
            EarlyError("No valid view ids supplied.");
            return response;
        }
        IList<ElementId> viewIds = viewIdInts.Select(i => new ElementId(i)).ToList();
        var remainingIds = new HashSet<long>(viewIdInts); // updated as views are placed

        // Validate sheet
        ViewSheet sheet = doc.GetElement(new ElementId(sheetIdInt)) as ViewSheet;
        if (sheet == null)
        {
            EarlyError($"ViewSheet with id {sheetIdInt} not found.", remainingIds.Select(i => i.ToString()));
            return response;
        }
        if (sheet.IsPlaceholder)
        {
            EarlyError("Selected sheet is a placeholder sheet.", remainingIds.Select(i => i.ToString()));
            return response;
        }

        // ---------- Locate title‑block & constants ----------
        var titleBlock = new FilteredElementCollector(doc, sheet.Id)
                           .OfCategory(BuiltInCategory.OST_TitleBlocks)
                           .OfClass(typeof(FamilyInstance))
                           .FirstOrDefault() as FamilyInstance;
        if (titleBlock == null)
        {
            EarlyError("No title block found on sheet.", remainingIds.Select(i => i.ToString()));
            return response;
        }

        var tbBox = titleBlock.get_BoundingBox(sheet);
        if (tbBox == null)
        {
            EarlyError("Bounding box of title block is null.", remainingIds.Select(i => i.ToString()));
            return response;
        }

        // --- Unit helpers ---
        const double FT_PER_MM = 1.0 / 304.8;
        double offsetRight = offsetRightMm * FT_PER_MM;
        double offsetBottom = 30 * FT_PER_MM;
        double spacing = 30 * FT_PER_MM;

        // Placement state trackers
        double columnRight = tbBox.Max.X - offsetRight;
        double currentBottom = tbBox.Min.Y + offsetBottom;
        double columnWidth = 0;

        int placedCount = 0;

        // ---------- Iterate views ----------
        foreach (var viewId in viewIds)
        {
            var view = doc.GetElement(viewId) as View;
            if (view == null)
                continue; // leave id in remainingIds

            using (var tx = new Transaction(doc, $"Place view {view.Title}"))
            {
                try
                {
                    tx.Start();

                    if (!Viewport.CanAddViewToSheet(doc, sheet.Id, viewId))
                        throw new Exception($"View '{view.Title}' (id {viewId.Value}) cannot be placed on the sheet.");

                    var vp = Viewport.Create(doc, sheet.Id, viewId, XYZ.Zero);
                    if (vp == null)
                        throw new Exception($"Failed to create viewport for view '{view.Title}' (id {viewId.Value}).");

                    var vpBox = vp.get_BoundingBox(sheet) ?? throw new Exception($"Viewport bounding box is null for view '{view.Title}'.");

                    double width = vpBox.Max.X - vpBox.Min.X;
                    double height = vpBox.Max.Y - vpBox.Min.Y;

                    bool fitsVertically = currentBottom + height <= tbBox.Max.Y - offsetBottom;
                    if (!fitsVertically)
                    {
                        columnRight -= (columnWidth + spacing);
                        columnWidth = 0;
                        currentBottom = tbBox.Min.Y + offsetBottom;
                    }

                    if (columnRight - width < tbBox.Min.X + spacing)
                        throw new Exception("Not enough horizontal space on sheet to place remaining views.");

                    double bottomLeftX = columnRight - width;
                    double bottomLeftY = currentBottom;
                    XYZ desiredCtr = new XYZ(bottomLeftX + width / 2, bottomLeftY + height / 2, 0);
                    XYZ curCtr = GetBoundingBoxCenter(vpBox) ?? throw new Exception($"Viewport center could not be determined for view '{view.Title}'.");

                    ElementTransformUtils.MoveElement(doc, vp.Id, desiredCtr - curCtr);

                    // Update trackers
                    currentBottom += height + spacing;
                    columnWidth = Math.Max(columnWidth, width);

                    tx.Commit();
                    placedCount++;
                    remainingIds.Remove(viewId.Value);
                }
                catch (Exception exView)
                {
                    tx.RollBack();
                    response["status"] = placedCount > 0 ? "partial" : "error";
                    response["placed"] = placedCount;
                    response["unplaced"] = remainingIds.Count;
                    response["remaining_view_ids"] = string.Join(",", remainingIds);
                    response["message"] = exView.Message;
                    return response;
                }
            }
        }

        // Success (some invalid ids may remain)
        response["status"] = remainingIds.Count == 0 ? "success" : "partial";
        response["placed"] = placedCount;
        response["unplaced"] = remainingIds.Count;
        if (remainingIds.Count > 0)
            response["remaining_view_ids"] = string.Join(",", remainingIds);
        response["message"] = remainingIds.Count == 0 ?
                               $"All {placedCount} view(s) placed successfully." :
                               "Some views could not be placed – see list.";
        return response;
    }

    private static XYZ GetBoundingBoxCenter(BoundingBoxXYZ bb)
    {
        if (bb == null) return null;
        return new XYZ((bb.Min.X + bb.Max.X) / 2.0,
                       (bb.Min.Y + bb.Max.Y) / 2.0,
                       (bb.Min.Z + bb.Max.Z) / 2.0);
    }
}
