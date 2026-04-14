// Batching enhancement for Revit to PostgreSQL export with safe chunked batching, including element types
using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public class BatchedPostgresDb : PostgresDb, IDisposable
{
    private NpgsqlConnection _conn;
    private NpgsqlTransaction _tx;
    private readonly string _connStr;

    private const int ChunkSize = 1000;
    private readonly List<string> _elementSqls = new List<string>();
    private readonly HashSet<int> _elementIds = new HashSet<int>();
    private class ParameterRow
    {
        public int ElementId;
        public string Name;
        public string Value;
        public bool IsType;
        public string[] Categories;
        public DateTime LastSaved;
        public string DocId;
    }

    private class TypeParameterRow
    {
        public int TypeId;
        public string Name;
        public string Value;
        public string[] Categories;
        public DateTime LastSaved;
        public string DocId;
    }

    private readonly Dictionary<string, ParameterRow> _paramSqlMap = new Dictionary<string, ParameterRow>();
    private readonly Dictionary<string, TypeParameterRow> _typeParamSqlMap = new Dictionary<string, TypeParameterRow>();
    private readonly List<string> _elementTypeSqls = new List<string>();
    private readonly HashSet<int> _elementTypeIds = new HashSet<int>();

    // Linked elements batching (scoped by host doc + link instance)
    private readonly List<string> _linkedElementSqls = new List<string>();
    private readonly HashSet<string> _linkedElementKeys = new HashSet<string>();
    private class LinkedParameterRow
    {
        public string HostDocId;
        public int LinkInstanceId;
        public int ElementId;
        public string Name;
        public string Value;
        public bool IsType;
        public string[] Categories;
        public DateTime LastSaved;
    }
    private readonly Dictionary<string, LinkedParameterRow> _linkedParamMap = new Dictionary<string, LinkedParameterRow>();
    // Linked element types batching
    private readonly List<string> _linkedElementTypeSqls = new List<string>();
    private readonly HashSet<string> _linkedElementTypeKeys = new HashSet<string>();


    public BatchedPostgresDb(string connectionString) : base(connectionString)
    {
        _connStr = connectionString;
    }

    private void LogError(string context, Exception ex)
    {
        string msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR in {context}:\n{ex}\n";
        try
        {
            System.IO.Directory.CreateDirectory(@"C:\Temp");
            System.IO.File.AppendAllText(@"C:\Temp\revit-export-error.log", msg);
        }
        catch { /* swallow log failures */ }
    }
    private void EnsureTransaction()
    {
        if (_conn != null) return;

        if (string.IsNullOrWhiteSpace(_connStr))
            throw new InvalidOperationException("PostgreSQL connection string is empty or null.");

        try
        {
            _conn = new NpgsqlConnection(_connStr);
            _conn.Open();
            _tx = _conn.BeginTransaction();

            // Optional: Print a message to Revit for debugging
            // Autodesk.Revit.UI.TaskDialog.Show("DB Connection", "Connection to PostgreSQL established.");
        }
        catch (Exception ex)
        {
            string error = $"Failed to open PostgreSQL connection.\n\nConnection String:\n{_connStr}\n\nError:\n{ex.Message}";
            Console.WriteLine(error);

            // Optional: Show in Revit if running in UI context
            // Autodesk.Revit.UI.TaskDialog.Show("Connection Error", error);

            throw new ApplicationException("Could not establish PostgreSQL connection. See inner exception for details.", ex);
        }
    }

    public void StageElement(int id, Guid guid, string name, string category,
        string typeName, string level, string docId, DateTime lastSaved)
    {
        EnsureTransaction();
        if (_elementIds.Contains(id)) return; // prevent duplicates

        string esc(string s) => s != null ? "'" + s.Replace("'", "''") + "'" : "NULL";
        _elementSqls.Add($"({id}, '{guid}', {esc(name)}, {esc(category)}, {esc(typeName)}, {esc(level)}, {esc(docId)}, '{lastSaved:O}')");
        _elementIds.Add(id);

        if (_elementSqls.Count >= ChunkSize) FlushElements();
    }

    public void StageParameter(int elementId, string name, string value, bool isType,
        string[] applicable, DateTime lastSaved, string docId)
    {
        EnsureTransaction();
        string key = elementId + "::" + name;
        _paramSqlMap[key] = new ParameterRow
        {
            ElementId = elementId,
            Name = name,
            Value = value,
            IsType = isType,
            Categories = applicable,
            LastSaved = lastSaved,
            DocId = docId
        };

        if (_paramSqlMap.Count >= ChunkSize) FlushParameters();
    }

    public void StageTypeParameter(int typeId, string name, string value, string[] applicable, DateTime lastSaved, string docId)
    {
        EnsureTransaction();
        string key = typeId + "::" + name;
        _typeParamSqlMap[key] = new TypeParameterRow
        {
            TypeId = typeId,
            Name = name,
            Value = value,
            Categories = applicable,
            LastSaved = lastSaved,
            DocId = docId
        };

        if (_typeParamSqlMap.Count >= ChunkSize) FlushTypeParameters();
    }

    private void FlushElements()
    {
        if (_elementSqls.Count == 0) return;
        try
        {
            string baseInsert = "INSERT INTO revit_elements (id, guid, name, category, type_name, level, doc_id, last_saved) VALUES "
                + string.Join(",\n", _elementSqls) + "\n";
            string sqlV2 = baseInsert + @"ON CONFLICT (doc_id, id) DO UPDATE SET
                    guid = EXCLUDED.guid,
                    name = EXCLUDED.name,
                    category = EXCLUDED.category,
                    type_name = EXCLUDED.type_name,
                    level = EXCLUDED.level,
                    doc_id = EXCLUDED.doc_id,
                    last_saved = EXCLUDED.last_saved
                WHERE EXCLUDED.last_saved > revit_elements.last_saved";
            string sqlV1 = baseInsert + @"ON CONFLICT (id) DO UPDATE SET
                    guid = EXCLUDED.guid,
                    name = EXCLUDED.name,
                    category = EXCLUDED.category,
                    type_name = EXCLUDED.type_name,
                    level = EXCLUDED.level,
                    doc_id = EXCLUDED.doc_id,
                    last_saved = EXCLUDED.last_saved
                WHERE EXCLUDED.last_saved > revit_elements.last_saved";
            new NpgsqlCommand("SAVEPOINT sp_flush", _conn, _tx).ExecuteNonQuery();
            try
            {
                new NpgsqlCommand(sqlV2, _conn, _tx).ExecuteNonQuery();
                new NpgsqlCommand("RELEASE SAVEPOINT sp_flush", _conn, _tx).ExecuteNonQuery();
            }
            catch (PostgresException)
            {
                new NpgsqlCommand("ROLLBACK TO SAVEPOINT sp_flush", _conn, _tx).ExecuteNonQuery();
                new NpgsqlCommand(sqlV1, _conn, _tx).ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            LogError("FlushElements", ex);
            throw;
        }
        _elementSqls.Clear();
        _elementIds.Clear();
    }

    private void FlushParameters()
    {
        if (_paramSqlMap.Count == 0) return;
        try
        {
            var sb = new StringBuilder();
            var cmd = new NpgsqlCommand(string.Empty, _conn, _tx);
            int i = 0;
            foreach (var row in _paramSqlMap.Values)
            {
                if (sb.Length > 0) sb.Append(",");
                sb.Append($"(@doc{i}, @el{i}, @name{i}, @val{i}, @typ{i}, @cat{i}, @ts{i})");

                cmd.Parameters.AddWithValue($"@doc{i}", (object)row.DocId ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@el{i}", row.ElementId);
                cmd.Parameters.AddWithValue($"@name{i}", (object)row.Name ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@val{i}", (object)row.Value ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@typ{i}", row.IsType);
                cmd.Parameters.AddWithValue($"@cat{i}", (object)row.Categories ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@ts{i}", row.LastSaved);
                i++;
            }

            string headV2 = "INSERT INTO revit_parameters (doc_id, element_id, param_name, param_value, is_type, applicable_categories, last_saved) VALUES ";
            cmd.CommandText = headV2 + sb.ToString() + @"
                ON CONFLICT (doc_id, element_id, param_name) DO UPDATE SET
                    param_value = EXCLUDED.param_value,
                    is_type = EXCLUDED.is_type,
                    applicable_categories = EXCLUDED.applicable_categories,
                    last_saved = EXCLUDED.last_saved
                WHERE EXCLUDED.last_saved > revit_parameters.last_saved";

            new NpgsqlCommand("SAVEPOINT sp_flush", _conn, _tx).ExecuteNonQuery();
            try
            {
                cmd.ExecuteNonQuery();
                new NpgsqlCommand("RELEASE SAVEPOINT sp_flush", _conn, _tx).ExecuteNonQuery();
            }
            catch (PostgresException)
            {
                new NpgsqlCommand("ROLLBACK TO SAVEPOINT sp_flush", _conn, _tx).ExecuteNonQuery();
                var cmdLegacy = new NpgsqlCommand(string.Empty, _conn, _tx);
                var sb2 = new StringBuilder();
                int j = 0;
                foreach (var row in _paramSqlMap.Values)
                {
                    if (sb2.Length > 0) sb2.Append(",");
                    sb2.Append($"(@elL{j}, @nameL{j}, @valL{j}, @typL{j}, @catL{j}, @tsL{j})");
                    cmdLegacy.Parameters.AddWithValue($"@elL{j}", row.ElementId);
                    cmdLegacy.Parameters.AddWithValue($"@nameL{j}", (object)row.Name ?? DBNull.Value);
                    cmdLegacy.Parameters.AddWithValue($"@valL{j}", (object)row.Value ?? DBNull.Value);
                    cmdLegacy.Parameters.AddWithValue($"@typL{j}", row.IsType);
                    cmdLegacy.Parameters.AddWithValue($"@catL{j}", (object)row.Categories ?? DBNull.Value);
                    cmdLegacy.Parameters.AddWithValue($"@tsL{j}", row.LastSaved);
                    j++;
                }
                cmdLegacy.CommandText = "INSERT INTO revit_parameters (element_id, param_name, param_value, is_type, applicable_categories, last_saved) VALUES " + sb2.ToString() + @"
                ON CONFLICT (element_id, param_name) DO UPDATE SET
                    param_value = EXCLUDED.param_value,
                    is_type = EXCLUDED.is_type,
                    applicable_categories = EXCLUDED.applicable_categories,
                    last_saved = EXCLUDED.last_saved
                WHERE EXCLUDED.last_saved > revit_parameters.last_saved";
                cmdLegacy.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            LogError("FlushParameters", ex);
            Console.WriteLine("Error flushing parameters: " + ex.Message);
            throw;
        }
        _paramSqlMap.Clear();
    }

    private void FlushTypeParameters()
    {
        if (_typeParamSqlMap.Count == 0) return;
        try
        {
            var sb = new StringBuilder();
            var cmd = new NpgsqlCommand(string.Empty, _conn, _tx);
            int i = 0;
            foreach (var row in _typeParamSqlMap.Values)
            {
                if (sb.Length > 0) sb.Append(",");
                sb.Append($"(@doc{i}, @tid{i}, @name{i}, @val{i}, @cat{i}, @ts{i})");

                cmd.Parameters.AddWithValue($"@doc{i}", (object)row.DocId ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@tid{i}", row.TypeId);
                cmd.Parameters.AddWithValue($"@name{i}", (object)row.Name ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@val{i}", (object)row.Value ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@cat{i}", (object)row.Categories ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@ts{i}", row.LastSaved);
                i++;
            }

            string headV2 = "INSERT INTO revit_type_parameters (doc_id, element_type_id, param_name, param_value, applicable_categories, last_saved) VALUES ";
            cmd.CommandText = headV2 + sb.ToString() + @"
                ON CONFLICT (doc_id, element_type_id, param_name) DO UPDATE SET
                    param_value = EXCLUDED.param_value,
                    applicable_categories = EXCLUDED.applicable_categories,
                    last_saved = EXCLUDED.last_saved
                WHERE EXCLUDED.last_saved > revit_type_parameters.last_saved";

            new NpgsqlCommand("SAVEPOINT sp_flush", _conn, _tx).ExecuteNonQuery();
            try
            {
                cmd.ExecuteNonQuery();
                new NpgsqlCommand("RELEASE SAVEPOINT sp_flush", _conn, _tx).ExecuteNonQuery();
            }
            catch (PostgresException)
            {
                new NpgsqlCommand("ROLLBACK TO SAVEPOINT sp_flush", _conn, _tx).ExecuteNonQuery();
                var cmdLegacy = new NpgsqlCommand(string.Empty, _conn, _tx);
                var sb2 = new StringBuilder();
                int j = 0;
                foreach (var row in _typeParamSqlMap.Values)
                {
                    if (sb2.Length > 0) sb2.Append(",");
                    sb2.Append($"(@tidL{j}, @nameL{j}, @valL{j}, @catL{j}, @tsL{j})");
                    cmdLegacy.Parameters.AddWithValue($"@tidL{j}", row.TypeId);
                    cmdLegacy.Parameters.AddWithValue($"@nameL{j}", (object)row.Name ?? DBNull.Value);
                    cmdLegacy.Parameters.AddWithValue($"@valL{j}", (object)row.Value ?? DBNull.Value);
                    cmdLegacy.Parameters.AddWithValue($"@catL{j}", (object)row.Categories ?? DBNull.Value);
                    cmdLegacy.Parameters.AddWithValue($"@tsL{j}", row.LastSaved);
                    j++;
                }
                cmdLegacy.CommandText = "INSERT INTO revit_type_parameters (element_type_id, param_name, param_value, applicable_categories, last_saved) VALUES " + sb2.ToString() + @"
                ON CONFLICT (element_type_id, param_name) DO UPDATE SET
                    param_value = EXCLUDED.param_value,
                    applicable_categories = EXCLUDED.applicable_categories,
                    last_saved = EXCLUDED.last_saved
                WHERE EXCLUDED.last_saved > revit_type_parameters.last_saved";
                cmdLegacy.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            LogError("FlushParameters", ex);
            Console.WriteLine("Error flushing parameters: " + ex.Message);
            throw;
        }
        _typeParamSqlMap.Clear();
    }

    public void StageElementType(int id, Guid guid, string family, string typeName, string category, string docId, DateTime lastSaved)
    {
        EnsureTransaction();
        if (_elementTypeIds.Contains(id)) return;

        string esc(string s) => s != null ? "'" + s.Replace("'", "''") + "'" : "NULL";
        _elementTypeSqls.Add($"({id}, '{guid}', {esc(family)}, {esc(typeName)}, {esc(category)}, {esc(docId)}, '{lastSaved:O}')");
        _elementTypeIds.Add(id);

        if (_elementTypeSqls.Count >= ChunkSize) FlushElementTypes();
    }

    private void FlushElementTypes()
    {
        if (_elementTypeSqls.Count == 0) return;
        try
        {
            string baseInsert = "INSERT INTO revit_elementtypes (id, guid, family, type_name, category, doc_id, last_saved) VALUES "
                + string.Join(",\n", _elementTypeSqls) + "\n";
            string sqlV2 = baseInsert + @"ON CONFLICT (doc_id, id) DO UPDATE SET
                    guid = EXCLUDED.guid,
                    family = EXCLUDED.family,
                    type_name = EXCLUDED.type_name,
                    category = EXCLUDED.category,
                    doc_id = EXCLUDED.doc_id,
                    last_saved = EXCLUDED.last_saved
                WHERE EXCLUDED.last_saved > revit_elementtypes.last_saved";
            string sqlV1 = baseInsert + @"ON CONFLICT (id) DO UPDATE SET
                    guid = EXCLUDED.guid,
                    family = EXCLUDED.family,
                    type_name = EXCLUDED.type_name,
                    category = EXCLUDED.category,
                    doc_id = EXCLUDED.doc_id,
                    last_saved = EXCLUDED.last_saved
                WHERE EXCLUDED.last_saved > revit_elementtypes.last_saved";
            new NpgsqlCommand("SAVEPOINT sp_flush", _conn, _tx).ExecuteNonQuery();
            try
            {
                new NpgsqlCommand(sqlV2, _conn, _tx).ExecuteNonQuery();
                new NpgsqlCommand("RELEASE SAVEPOINT sp_flush", _conn, _tx).ExecuteNonQuery();
            }
            catch (PostgresException)
            {
                new NpgsqlCommand("ROLLBACK TO SAVEPOINT sp_flush", _conn, _tx).ExecuteNonQuery();
                new NpgsqlCommand(sqlV1, _conn, _tx).ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            LogError("FlushElementTypes", ex);
            throw;
        }
        _elementTypeSqls.Clear();
        _elementTypeIds.Clear();
    }

    public void CommitAll()
    {
        try
        {
            FlushElements();
            FlushElementTypes();
            FlushParameters();
            FlushTypeParameters();
            FlushLinkedElements();
            FlushLinkedParameters();
            FlushLinkedElementTypes();
            _tx?.Commit();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Commit failed: " + ex.Message);
            throw;
        }
        finally
        {
            _tx?.Dispose();
            _conn?.Close();
            _conn?.Dispose();
            _tx = null;
            _conn = null;
        }
    }

    public void Dispose()
    {
        _tx?.Dispose();
        _conn?.Dispose();
    }

    public void StageLinkedElement(string hostDocId, int linkInstanceId, string linkDocId,
        int id, Guid guid, string name, string category, string typeName, string level, DateTime lastSaved)
    {
        EnsureTransaction();
        string key = hostDocId + "|" + linkInstanceId + "|" + id;
        if (_linkedElementKeys.Contains(key)) return;
        string esc(string s) => s != null ? "'" + s.Replace("'", "''") + "'" : "NULL";
        _linkedElementSqls.Add($"({esc(hostDocId)}, {linkInstanceId}, {esc(linkDocId)}, {id}, '{guid}', {esc(name)}, {esc(category)}, {esc(typeName)}, {esc(level)}, '{lastSaved:O}')");
        _linkedElementKeys.Add(key);
        if (_linkedElementSqls.Count >= ChunkSize) FlushLinkedElements();
    }

    public void StageLinkedParameter(string hostDocId, int linkInstanceId, int elementId, string name, string value, bool isType,
        string[] applicable, DateTime lastSaved)
    {
        EnsureTransaction();
        string key = hostDocId + "|" + linkInstanceId + "|" + elementId + "::" + name;
        _linkedParamMap[key] = new LinkedParameterRow
        {
            HostDocId = hostDocId,
            LinkInstanceId = linkInstanceId,
            ElementId = elementId,
            Name = name,
            Value = value,
            IsType = isType,
            Categories = applicable,
            LastSaved = lastSaved
        };
        if (_linkedParamMap.Count >= ChunkSize) FlushLinkedParameters();
    }

    private void FlushLinkedElements()
    {
        if (_linkedElementSqls.Count == 0) return;
        try
        {
            string head = "INSERT INTO revit_linked_elements (host_doc_id, link_instance_id, link_doc_id, id, guid, name, category, type_name, level, last_saved) VALUES ";
            string sql = head + string.Join(",\n", _linkedElementSqls) + @"
                ON CONFLICT (host_doc_id, link_instance_id, id) DO UPDATE SET
                    link_doc_id = EXCLUDED.link_doc_id,
                    guid = EXCLUDED.guid,
                    name = EXCLUDED.name,
                    category = EXCLUDED.category,
                    type_name = EXCLUDED.type_name,
                    level = EXCLUDED.level,
                    last_saved = EXCLUDED.last_saved
                WHERE EXCLUDED.last_saved > revit_linked_elements.last_saved";
            new NpgsqlCommand(sql, _conn, _tx).ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            LogError("FlushLinkedElements", ex);
            throw;
        }
        _linkedElementSqls.Clear();
        _linkedElementKeys.Clear();
    }

    private void FlushLinkedParameters()
    {
        if (_linkedParamMap.Count == 0) return;
        try
        {
            var sb = new StringBuilder();
            var cmd = new NpgsqlCommand(string.Empty, _conn, _tx);
            int i = 0;
            foreach (var row in _linkedParamMap.Values)
            {
                if (sb.Length > 0) sb.Append(",");
                sb.Append($"(@host{i}, @iid{i}, @el{i}, @name{i}, @val{i}, @typ{i}, @cat{i}, @ts{i})");
                cmd.Parameters.AddWithValue($"@host{i}", (object)row.HostDocId ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@iid{i}", row.LinkInstanceId);
                cmd.Parameters.AddWithValue($"@el{i}", row.ElementId);
                cmd.Parameters.AddWithValue($"@name{i}", (object)row.Name ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@val{i}", (object)row.Value ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@typ{i}", row.IsType);
                cmd.Parameters.AddWithValue($"@cat{i}", (object)row.Categories ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@ts{i}", row.LastSaved);
                i++;
            }
            cmd.CommandText = "INSERT INTO revit_linked_parameters (host_doc_id, link_instance_id, element_id, param_name, param_value, is_type, applicable_categories, last_saved) VALUES "
                + sb.ToString() + @"
                ON CONFLICT (host_doc_id, link_instance_id, element_id, param_name) DO UPDATE SET
                    param_value = EXCLUDED.param_value,
                    is_type = EXCLUDED.is_type,
                    applicable_categories = EXCLUDED.applicable_categories,
                    last_saved = EXCLUDED.last_saved
                WHERE EXCLUDED.last_saved > revit_linked_parameters.last_saved";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            LogError("FlushLinkedParameters", ex);
            throw;
        }
        _linkedParamMap.Clear();
    }

    public void StageLinkedElementType(string hostDocId, int linkInstanceId, string linkDocId,
        int id, Guid guid, string family, string typeName, string category, DateTime lastSaved)
    {
        EnsureTransaction();
        string key = hostDocId + "|" + linkInstanceId + "|" + id;
        if (_linkedElementTypeKeys.Contains(key)) return;
        string esc(string s) => s != null ? "'" + s.Replace("'", "''") + "'" : "NULL";
        _linkedElementTypeSqls.Add($"({esc(hostDocId)}, {linkInstanceId}, {esc(linkDocId)}, {id}, '{guid}', {esc(family)}, {esc(typeName)}, {esc(category)}, '{lastSaved:O}')");
        _linkedElementTypeKeys.Add(key);
        if (_linkedElementTypeSqls.Count >= ChunkSize) FlushLinkedElementTypes();
    }

    private void FlushLinkedElementTypes()
    {
        if (_linkedElementTypeSqls.Count == 0) return;
        try
        {
            string head = "INSERT INTO revit_linked_elementtypes (host_doc_id, link_instance_id, link_doc_id, id, guid, family, type_name, category, last_saved) VALUES ";
            string sql = head + string.Join(",\n", _linkedElementTypeSqls) + @"
                ON CONFLICT (host_doc_id, link_instance_id, id) DO UPDATE SET
                    link_doc_id = EXCLUDED.link_doc_id,
                    guid = EXCLUDED.guid,
                    family = EXCLUDED.family,
                    type_name = EXCLUDED.type_name,
                    category = EXCLUDED.category,
                    last_saved = EXCLUDED.last_saved
                WHERE EXCLUDED.last_saved > revit_linked_elementtypes.last_saved";
            new NpgsqlCommand(sql, _conn, _tx).ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            LogError("FlushLinkedElementTypes", ex);
            throw;
        }
        _linkedElementTypeSqls.Clear();
        _linkedElementTypeKeys.Clear();
    }

    // Prune stale rows older than current session
    public void PruneHostStaleInternal(string docId, DateTime lastSaved)
    {
        EnsureTransaction();
        string[] sqls = new[]
        {
            "DELETE FROM revit_parameters WHERE doc_id=@doc AND last_saved < @ts",
            "DELETE FROM revit_type_parameters WHERE doc_id=@doc AND last_saved < @ts",
            "DELETE FROM revit_elements WHERE doc_id=@doc AND last_saved < @ts",
            "DELETE FROM revit_elementtypes WHERE doc_id=@doc AND last_saved < @ts"
        };
        foreach (var s in sqls)
        {
            new NpgsqlCommand("SAVEPOINT sp_prune", _conn, _tx).ExecuteNonQuery();
            try
            {
                var cmd = new NpgsqlCommand(s, _conn, _tx);
                cmd.Parameters.AddWithValue("@doc", (object)docId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ts", lastSaved);
                cmd.ExecuteNonQuery();
                new NpgsqlCommand("RELEASE SAVEPOINT sp_prune", _conn, _tx).ExecuteNonQuery();
            }
            catch (PostgresException ex)
            {
                new NpgsqlCommand("ROLLBACK TO SAVEPOINT sp_prune", _conn, _tx).ExecuteNonQuery();
                LogError("PruneHostStaleInternal", ex);
            }
        }
    }

    public void PruneLinkedStaleInternal(string hostDocId, int linkInstanceId, DateTime lastSaved)
    {
        EnsureTransaction();
        string[] sqls = new[]
        {
            "DELETE FROM revit_linked_parameters WHERE host_doc_id=@host AND link_instance_id=@iid AND last_saved < @ts",
            "DELETE FROM revit_linked_elements WHERE host_doc_id=@host AND link_instance_id=@iid AND last_saved < @ts",
            "DELETE FROM revit_linked_elementtypes WHERE host_doc_id=@host AND link_instance_id=@iid AND last_saved < @ts"
        };
        foreach (var s in sqls)
        {
            new NpgsqlCommand("SAVEPOINT sp_prune", _conn, _tx).ExecuteNonQuery();
            try
            {
                var cmd = new NpgsqlCommand(s, _conn, _tx);
                cmd.Parameters.AddWithValue("@host", (object)hostDocId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@iid", linkInstanceId);
                cmd.Parameters.AddWithValue("@ts", lastSaved);
                cmd.ExecuteNonQuery();
                new NpgsqlCommand("RELEASE SAVEPOINT sp_prune", _conn, _tx).ExecuteNonQuery();
            }
            catch (PostgresException ex)
            {
                new NpgsqlCommand("ROLLBACK TO SAVEPOINT sp_prune", _conn, _tx).ExecuteNonQuery();
                LogError("PruneLinkedStaleInternal", ex);
            }
        }
    }
}
