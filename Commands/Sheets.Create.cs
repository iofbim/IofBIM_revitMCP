// CreateSheetCommand.cs - Creates a new sheet from a title block
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

public class CreateSheetCommand : ICommand
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

        if (!input.TryGetValue("title_block_name", out var titleBlockName))
        {
            response["status"] = "error";
            response["message"] = "Missing required 'title_block_name'.";
            return response;
        }

        try
        {
            var titleBlock = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(tb => tb.Name.Equals(titleBlockName, StringComparison.OrdinalIgnoreCase));

            if (titleBlock == null)
            {
                response["status"] = "error";
                response["message"] = $"Title block '{titleBlockName}' not found.";
                return response;
            }

            if (!titleBlock.IsActive)
            {
                using (var tx = new Transaction(doc, "Activate Title Block"))
                {
                    tx.Start();
                    titleBlock.Activate();
                    tx.Commit();
                }
            }

            ViewSheet newSheet = null;
            using (var tx = new Transaction(doc, "Create Sheet"))
            {
                tx.Start();
                newSheet = ViewSheet.Create(doc, titleBlock.Id);
                tx.Commit();
            }

            if (newSheet != null)
            {
                response["status"] = "success";
                response["sheet_id"] = newSheet.Id.Value;
                response["sheet_name"] = newSheet.Name;
                response["sheet_number"] = newSheet.SheetNumber;
            }
            else
            {
                response["status"] = "error";
                response["message"] = "Failed to create sheet.";
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
