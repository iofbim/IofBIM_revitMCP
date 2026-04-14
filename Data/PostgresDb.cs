using Npgsql;
using NpgsqlTypes;
using System;
using System.Collections.Generic;
using System.Data;

/// <summary>
/// Minimal PostgreSQL helper for executing commands and queries.
/// Connection string should be provided via constructor.
/// </summary>
public class PostgresDb
{
    private readonly string _connectionString;

    public PostgresDb(string connectionString)
    {
        _connectionString = connectionString;
    }

    public int ExecuteNonQuery(string sql, params NpgsqlParameter[] args)
    {
        using (var conn = new NpgsqlConnection(_connectionString))
        {
            conn.Open();
            return ExecuteNonQuery(conn, null, sql, args);
        }
    }

    public int ExecuteNonQuery(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params NpgsqlParameter[] args)
    {
        using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            if (args != null)
                cmd.Parameters.AddRange(args);
            int result = cmd.ExecuteNonQuery();
            cmd.Parameters.Clear(); // release parameters so they can be reused in fallback calls
            return result;
        }
    }

    public List<Dictionary<string, object>> Query(string sql, params NpgsqlParameter[] args)
    {
        var results = new List<Dictionary<string, object>>();
        using (var conn = new NpgsqlConnection(_connectionString))
        using (var cmd = new NpgsqlCommand(sql, conn))
        {
            if (args != null)
                cmd.Parameters.AddRange(args);
            conn.Open();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var row = new Dictionary<string, object>();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = reader.GetValue(i);
                    }
                    results.Add(row);
                }
            }
        }
        return results;
    }

    public void UpsertElement(int id, Guid guid, string name, string category,
        string typeName, string level, string docId, DateTime lastSaved,
        NpgsqlConnection conn = null, NpgsqlTransaction tx = null)
    {
        string sqlV2 = @"INSERT INTO revit_elements
            (id, guid, name, category, type_name, level, doc_id, last_saved)
            VALUES (@id, @guid, @name, @category, @type_name, @level, @doc_id, @last_saved)
            ON CONFLICT (doc_id, id) DO UPDATE SET
                guid = EXCLUDED.guid,
                name = EXCLUDED.name,
                category = EXCLUDED.category,
                type_name = EXCLUDED.type_name,
                level = EXCLUDED.level,
                doc_id = EXCLUDED.doc_id,
                last_saved = EXCLUDED.last_saved
            WHERE EXCLUDED.last_saved > revit_elements.last_saved";
        string sqlV1 = @"INSERT INTO revit_elements
            (id, guid, name, category, type_name, level, doc_id, last_saved)
            VALUES (@id, @guid, @name, @category, @type_name, @level, @doc_id, @last_saved)
            ON CONFLICT (id) DO UPDATE SET
                guid = EXCLUDED.guid,
                name = EXCLUDED.name,
                category = EXCLUDED.category,
                type_name = EXCLUDED.type_name,
                level = EXCLUDED.level,
                doc_id = EXCLUDED.doc_id,
                last_saved = EXCLUDED.last_saved
            WHERE EXCLUDED.last_saved > revit_elements.last_saved";

        var args = new[] {
            new NpgsqlParameter("@id", id),
            new NpgsqlParameter("@guid", guid),
            new NpgsqlParameter("@name", name ?? (object)DBNull.Value),
            new NpgsqlParameter("@category", category ?? (object)DBNull.Value),
            new NpgsqlParameter("@type_name", typeName ?? (object)DBNull.Value),
            new NpgsqlParameter("@level", level ?? (object)DBNull.Value),
            new NpgsqlParameter("@doc_id", docId ?? (object)DBNull.Value),
            new NpgsqlParameter("@last_saved", lastSaved)
        };

        try
        {
            if (conn == null)
                ExecuteNonQuery(sqlV2, args);
            else
                ExecuteNonQuery(conn, tx, sqlV2, args);
        }
        catch (PostgresException)
        {
            if (conn == null)
                ExecuteNonQuery(sqlV1, args);
            else
                ExecuteNonQuery(conn, tx, sqlV1, args);
        }
    }

    public void UpsertParameter(int elementId, string name, string value, bool isType,
        string[] applicable, DateTime lastSaved, string docId,
        NpgsqlConnection conn = null, NpgsqlTransaction tx = null)
    {
        string sqlV2 = @"INSERT INTO revit_parameters
            (doc_id, element_id, param_name, param_value, is_type, applicable_categories, last_saved)
            VALUES (@doc, @elid, @name, @value, @is_type, @categories, @last_saved)
            ON CONFLICT (doc_id, element_id, param_name) DO UPDATE SET
                param_value = EXCLUDED.param_value,
                is_type = EXCLUDED.is_type,
                applicable_categories = EXCLUDED.applicable_categories,
                last_saved = EXCLUDED.last_saved
            WHERE EXCLUDED.last_saved > revit_parameters.last_saved";
        string sqlV1 = @"INSERT INTO revit_parameters
            (element_id, param_name, param_value, is_type, applicable_categories, last_saved)
            VALUES (@elid, @name, @value, @is_type, @categories, @last_saved)
            ON CONFLICT (element_id, param_name) DO UPDATE SET
                param_value = EXCLUDED.param_value,
                is_type = EXCLUDED.is_type,
                applicable_categories = EXCLUDED.applicable_categories,
                last_saved = EXCLUDED.last_saved
            WHERE EXCLUDED.last_saved > revit_parameters.last_saved";

        var args = new[] {
            new NpgsqlParameter("@elid", elementId),
            new NpgsqlParameter("@name", name),
            new NpgsqlParameter("@value", value ?? (object)DBNull.Value),
            new NpgsqlParameter("@is_type", isType),
            new NpgsqlParameter("@categories", applicable ?? (object)DBNull.Value),
            new NpgsqlParameter("@doc", docId ?? (object)DBNull.Value),
            new NpgsqlParameter("@last_saved", lastSaved)
        };

        try
        {
            if (conn == null)
                ExecuteNonQuery(sqlV2, args);
            else
                ExecuteNonQuery(conn, tx, sqlV2, args);
        }
        catch (PostgresException)
        {
            // fallback to legacy schema without doc_id and composite unique
            var legacyArgs = new[] {
                new NpgsqlParameter("@elid", elementId),
                new NpgsqlParameter("@name", name),
                new NpgsqlParameter("@value", value ?? (object)DBNull.Value),
                new NpgsqlParameter("@is_type", isType),
                new NpgsqlParameter("@categories", applicable ?? (object)DBNull.Value),
                new NpgsqlParameter("@last_saved", lastSaved)
            };
            if (conn == null)
                ExecuteNonQuery(sqlV1, legacyArgs);
            else
                ExecuteNonQuery(conn, tx, sqlV1, legacyArgs);
        }
    }

    public void UpsertCategory(string enumVal, string name, string group, string description, Guid guid, DateTime lastSaved,
        NpgsqlConnection conn = null, NpgsqlTransaction tx = null)
    {
        string sql = @"INSERT INTO revit_categories
            (enum, name, category_group, description, guid, last_saved)
            VALUES (@enum, @name, @group, @description, @guid, @last_saved)
            ON CONFLICT (enum) DO UPDATE SET
                name = EXCLUDED.name,
                category_group = EXCLUDED.category_group,
                description = EXCLUDED.description,
                guid = EXCLUDED.guid,
                last_saved = EXCLUDED.last_saved
            WHERE EXCLUDED.last_saved > revit_categories.last_saved";

        var args = new[] {
            new NpgsqlParameter("@enum", enumVal),
            new NpgsqlParameter("@name", name ?? (object)DBNull.Value),
            new NpgsqlParameter("@group", group ?? (object)DBNull.Value),
            new NpgsqlParameter("@description", description ?? (object)DBNull.Value),
            new NpgsqlParameter("@guid", guid),
            new NpgsqlParameter("@last_saved", lastSaved)
        };

        if (conn == null)
            ExecuteNonQuery(sql, args);
        else
            ExecuteNonQuery(conn, tx, sql, args);
    }

    public void UpsertView(int id, Guid guid, string name, string viewType, int scale,
        string discipline, string detail, int? sheetId, string docId, DateTime lastSaved,
        NpgsqlConnection conn = null, NpgsqlTransaction tx = null)
    {
        string sql = @"INSERT INTO revit_views
            (id, guid, name, view_type, scale, discipline, detail_level, associated_sheet_id, doc_id, last_saved)
            VALUES (@id, @guid, @name, @type, @scale, @disc, @detail, @sheet, @doc, @last_saved)
            ON CONFLICT (id) DO UPDATE SET
                guid = EXCLUDED.guid,
                name = EXCLUDED.name,
                view_type = EXCLUDED.view_type,
                scale = EXCLUDED.scale,
                discipline = EXCLUDED.discipline,
                detail_level = EXCLUDED.detail_level,
                associated_sheet_id = EXCLUDED.associated_sheet_id,
                doc_id = EXCLUDED.doc_id,
                last_saved = EXCLUDED.last_saved
            WHERE EXCLUDED.last_saved > revit_views.last_saved";

        var args = new[] {
            new NpgsqlParameter("@id", id),
            new NpgsqlParameter("@guid", guid),
            new NpgsqlParameter("@name", name ?? (object)DBNull.Value),
            new NpgsqlParameter("@type", viewType ?? (object)DBNull.Value),
            new NpgsqlParameter("@scale", scale),
            new NpgsqlParameter("@disc", discipline ?? (object)DBNull.Value),
            new NpgsqlParameter("@detail", detail ?? (object)DBNull.Value),
            new NpgsqlParameter("@sheet", sheetId ?? (object)DBNull.Value),
            new NpgsqlParameter("@doc", docId ?? (object)DBNull.Value),
            new NpgsqlParameter("@last_saved", lastSaved)
        };

        if (conn == null)
            ExecuteNonQuery(sql, args);
        else
            ExecuteNonQuery(conn, tx, sql, args);
    }

    public void UpsertSheet(int id, Guid guid, string name, string number, string titleBlock, string docId, DateTime lastSaved,
        NpgsqlConnection conn = null, NpgsqlTransaction tx = null)
    {
        string sql = @"INSERT INTO revit_sheets
            (id, guid, name, number, title_block, doc_id, last_saved)
            VALUES (@id, @guid, @name, @num, @tb, @doc, @last_saved)
            ON CONFLICT (id) DO UPDATE SET
                guid = EXCLUDED.guid,
                name = EXCLUDED.name,
                number = EXCLUDED.number,
                title_block = EXCLUDED.title_block,
                doc_id = EXCLUDED.doc_id,
                last_saved = EXCLUDED.last_saved
            WHERE EXCLUDED.last_saved > revit_sheets.last_saved";

        var args = new[] {
            new NpgsqlParameter("@id", id),
            new NpgsqlParameter("@guid", guid),
            new NpgsqlParameter("@name", name ?? (object)DBNull.Value),
            new NpgsqlParameter("@num", number ?? (object)DBNull.Value),
            new NpgsqlParameter("@tb", titleBlock ?? (object)DBNull.Value),
            new NpgsqlParameter("@doc", docId ?? (object)DBNull.Value),
            new NpgsqlParameter("@last_saved", lastSaved)
        };

        if (conn == null)
            ExecuteNonQuery(sql, args);
        else
            ExecuteNonQuery(conn, tx, sql, args);
    }

    public void UpsertSchedule(int id, Guid guid, string name, string category, string docId, DateTime lastSaved,
        NpgsqlConnection conn = null, NpgsqlTransaction tx = null)
    {
        string sql = @"INSERT INTO revit_schedules
            (id, guid, name, category, doc_id, last_saved)
            VALUES (@id, @guid, @name, @cat, @doc, @last_saved)
            ON CONFLICT (id) DO UPDATE SET
                guid = EXCLUDED.guid,
                name = EXCLUDED.name,
                category = EXCLUDED.category,
                doc_id = EXCLUDED.doc_id,
                last_saved = EXCLUDED.last_saved
            WHERE EXCLUDED.last_saved > revit_schedules.last_saved";

        var args = new[] {
            new NpgsqlParameter("@id", id),
            new NpgsqlParameter("@guid", guid),
            new NpgsqlParameter("@name", name ?? (object)DBNull.Value),
            new NpgsqlParameter("@cat", category ?? (object)DBNull.Value),
            new NpgsqlParameter("@doc", docId ?? (object)DBNull.Value),
            new NpgsqlParameter("@last_saved", lastSaved)
        };

        if (conn == null)
            ExecuteNonQuery(sql, args);
        else
            ExecuteNonQuery(conn, tx, sql, args);
    }

    public void UpsertFamily(string name, string familyType, string category, string guid, string docId, DateTime lastSaved,
        NpgsqlConnection conn = null, NpgsqlTransaction tx = null)
    {
        string sqlV2 = @"INSERT INTO revit_families
            (name, family_type, category, guid, doc_id, last_saved)
            VALUES (@name, @type, @cat, @guid, @doc, @last_saved)
            ON CONFLICT (doc_id, name, family_type, category) DO UPDATE SET
                guid = EXCLUDED.guid,
                doc_id = EXCLUDED.doc_id,
                last_saved = EXCLUDED.last_saved
            WHERE EXCLUDED.last_saved > revit_families.last_saved";
        string sqlV1 = @"INSERT INTO revit_families
            (name, family_type, category, guid, doc_id, last_saved)
            VALUES (@name, @type, @cat, @guid, @doc, @last_saved)
            ON CONFLICT (name, family_type, category) DO UPDATE SET
                guid = EXCLUDED.guid,
                doc_id = EXCLUDED.doc_id,
                last_saved = EXCLUDED.last_saved
            WHERE EXCLUDED.last_saved > revit_families.last_saved";

        var args = new[] {
            new NpgsqlParameter("@name", name ?? (object)DBNull.Value),
            new NpgsqlParameter("@type", familyType ?? (object)DBNull.Value),
            new NpgsqlParameter("@cat", category ?? (object)DBNull.Value),
            new NpgsqlParameter("@guid", guid ?? (object)DBNull.Value),
            new NpgsqlParameter("@doc", docId ?? (object)DBNull.Value),
            new NpgsqlParameter("@last_saved", lastSaved)
        };

        try
        {
            if (conn == null)
                ExecuteNonQuery(sqlV2, args);
            else
                ExecuteNonQuery(conn, tx, sqlV2, args);
        }
        catch (PostgresException)
        {
            if (conn == null)
                ExecuteNonQuery(sqlV1, args);
            else
                ExecuteNonQuery(conn, tx, sqlV1, args);
        }
    }

    public void UpsertElementType(int id, Guid guid, string family, string typeName,
        string category, string docId, DateTime lastSaved,
        NpgsqlConnection conn = null, NpgsqlTransaction tx = null)
    {
        string sqlV2 = @"INSERT INTO revit_elementTypes
            (id, guid, family, type_name, category, doc_id, last_saved)
            VALUES (@id, @guid, @family, @type_name, @category, @doc_id, @last_saved)
            ON CONFLICT (doc_id, id) DO UPDATE SET
                guid = EXCLUDED.guid,
                family = EXCLUDED.family,
                type_name = EXCLUDED.type_name,
                category = EXCLUDED.category,
                doc_id = EXCLUDED.doc_id,
                last_saved = EXCLUDED.last_saved
            WHERE EXCLUDED.last_saved > revit_elementTypes.last_saved";
        string sqlV1 = @"INSERT INTO revit_elementTypes
            (id, guid, family, type_name, category, doc_id, last_saved)
            VALUES (@id, @guid, @family, @type_name, @category, @doc_id, @last_saved)
            ON CONFLICT (id) DO UPDATE SET
                guid = EXCLUDED.guid,
                family = EXCLUDED.family,
                type_name = EXCLUDED.type_name,
                category = EXCLUDED.category,
                doc_id = EXCLUDED.doc_id,
                last_saved = EXCLUDED.last_saved
            WHERE EXCLUDED.last_saved > revit_elementTypes.last_saved";

        var args = new[] {
            new NpgsqlParameter("@id", id),
            new NpgsqlParameter("@guid", guid),
            new NpgsqlParameter("@family", family ?? (object)DBNull.Value),
            new NpgsqlParameter("@type_name", typeName ?? (object)DBNull.Value),
            new NpgsqlParameter("@category", category ?? (object)DBNull.Value),
            new NpgsqlParameter("@doc_id", docId ?? (object)DBNull.Value),
            new NpgsqlParameter("@last_saved", lastSaved)
        };

        try
        {
            if (conn == null)
                ExecuteNonQuery(sqlV2, args);
            else
                ExecuteNonQuery(conn, tx, sqlV2, args);
        }
        catch (PostgresException)
        {
            if (conn == null)
                ExecuteNonQuery(sqlV1, args);
            else
                ExecuteNonQuery(conn, tx, sqlV1, args);
        }
    }

    public void UpsertLinkInstance(
        string hostDocId,
        int instanceId,
        string linkDocId,
        double? originX,
        double? originY,
        double? originZ,
        double? bxX,
        double? bxY,
        double? bxZ,
        double? byX,
        double? byY,
        double? byZ,
        double? bzX,
        double? bzY,
        double? bzZ,
        double? rotationZRadians,
        double? angleToTrueNorthRadians,
        DateTime lastSaved,
        NpgsqlConnection conn = null,
        NpgsqlTransaction tx = null)
    {
        string sqlV2 = @"CREATE TABLE IF NOT EXISTS revit_link_instances (
  host_doc_id TEXT NOT NULL,
  instance_id INTEGER NOT NULL,
  link_doc_id TEXT NOT NULL,
  origin_x DOUBLE PRECISION,
  origin_y DOUBLE PRECISION,
  origin_z DOUBLE PRECISION,
  basisx_x DOUBLE PRECISION,
  basisx_y DOUBLE PRECISION,
  basisx_z DOUBLE PRECISION,
  basisy_x DOUBLE PRECISION,
  basisy_y DOUBLE PRECISION,
  basisy_z DOUBLE PRECISION,
  basisz_x DOUBLE PRECISION,
  basisz_y DOUBLE PRECISION,
  basisz_z DOUBLE PRECISION,
  rotation_z_radians DOUBLE PRECISION,
  angle_to_true_north_radians DOUBLE PRECISION,
  last_saved TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT pk_revit_link_instances PRIMARY KEY (host_doc_id, instance_id)
);
INSERT INTO revit_link_instances (
  host_doc_id, instance_id, link_doc_id,
  origin_x, origin_y, origin_z,
  basisx_x, basisx_y, basisx_z,
  basisy_x, basisy_y, basisy_z,
  basisz_x, basisz_y, basisz_z,
  rotation_z_radians, angle_to_true_north_radians, last_saved)
VALUES (
  @host, @iid, @link,
  @ox, @oy, @oz,
  @bxx, @bxy, @bxz,
  @byx, @byy, @byz,
  @bzx, @bzy, @bzz,
  @rotz, @atn, @ts)
ON CONFLICT (host_doc_id, instance_id) DO UPDATE SET
  host_doc_id = EXCLUDED.host_doc_id,
  link_doc_id = EXCLUDED.link_doc_id,
  origin_x = EXCLUDED.origin_x,
  origin_y = EXCLUDED.origin_y,
  origin_z = EXCLUDED.origin_z,
  basisx_x = EXCLUDED.basisx_x,
  basisx_y = EXCLUDED.basisx_y,
  basisx_z = EXCLUDED.basisx_z,
  basisy_x = EXCLUDED.basisy_x,
  basisy_y = EXCLUDED.basisy_y,
  basisy_z = EXCLUDED.basisy_z,
  basisz_x = EXCLUDED.basisz_x,
  basisz_y = EXCLUDED.basisz_y,
  basisz_z = EXCLUDED.basisz_z,
  rotation_z_radians = EXCLUDED.rotation_z_radians,
  angle_to_true_north_radians = EXCLUDED.angle_to_true_north_radians,
  last_saved = EXCLUDED.last_saved";

        var args = new[]
        {
            new NpgsqlParameter("@host", hostDocId ?? (object)DBNull.Value),
            new NpgsqlParameter("@iid", instanceId),
            new NpgsqlParameter("@link", linkDocId ?? (object)DBNull.Value),
            new NpgsqlParameter("@ox", originX.HasValue ? (object)originX.Value : DBNull.Value),
            new NpgsqlParameter("@oy", originY.HasValue ? (object)originY.Value : DBNull.Value),
            new NpgsqlParameter("@oz", originZ.HasValue ? (object)originZ.Value : DBNull.Value),
            new NpgsqlParameter("@bxx", bxX.HasValue ? (object)bxX.Value : DBNull.Value),
            new NpgsqlParameter("@bxy", bxY.HasValue ? (object)bxY.Value : DBNull.Value),
            new NpgsqlParameter("@bxz", bxZ.HasValue ? (object)bxZ.Value : DBNull.Value),
            new NpgsqlParameter("@byx", byX.HasValue ? (object)byX.Value : DBNull.Value),
            new NpgsqlParameter("@byy", byY.HasValue ? (object)byY.Value : DBNull.Value),
            new NpgsqlParameter("@byz", byZ.HasValue ? (object)byZ.Value : DBNull.Value),
            new NpgsqlParameter("@bzx", bzX.HasValue ? (object)bzX.Value : DBNull.Value),
            new NpgsqlParameter("@bzy", bzY.HasValue ? (object)bzY.Value : DBNull.Value),
            new NpgsqlParameter("@bzz", bzZ.HasValue ? (object)bzZ.Value : DBNull.Value),
            new NpgsqlParameter("@rotz", rotationZRadians.HasValue ? (object)rotationZRadians.Value : DBNull.Value),
            new NpgsqlParameter("@atn", angleToTrueNorthRadians.HasValue ? (object)angleToTrueNorthRadians.Value : DBNull.Value),
            new NpgsqlParameter("@ts", lastSaved)
        };

        string sqlV1 = @"CREATE TABLE IF NOT EXISTS revit_link_instances (
  host_doc_id TEXT NOT NULL,
  instance_id INTEGER PRIMARY KEY,
  link_doc_id TEXT NOT NULL,
  origin_x DOUBLE PRECISION,
  origin_y DOUBLE PRECISION,
  origin_z DOUBLE PRECISION,
  basisx_x DOUBLE PRECISION,
  basisx_y DOUBLE PRECISION,
  basisx_z DOUBLE PRECISION,
  basisy_x DOUBLE PRECISION,
  basisy_y DOUBLE PRECISION,
  basisy_z DOUBLE PRECISION,
  basisz_x DOUBLE PRECISION,
  basisz_y DOUBLE PRECISION,
  basisz_z DOUBLE PRECISION,
  rotation_z_radians DOUBLE PRECISION,
  angle_to_true_north_radians DOUBLE PRECISION,
  last_saved TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);
INSERT INTO revit_link_instances (
  host_doc_id, instance_id, link_doc_id,
  origin_x, origin_y, origin_z,
  basisx_x, basisx_y, basisx_z,
  basisy_x, basisy_y, basisy_z,
  basisz_x, basisz_y, basisz_z,
  rotation_z_radians, angle_to_true_north_radians, last_saved)
VALUES (
  @host, @iid, @link,
  @ox, @oy, @oz,
  @bxx, @bxy, @bxz,
  @byx, @byy, @byz,
  @bzx, @bzy, @bzz,
  @rotz, @atn, @ts)
ON CONFLICT (instance_id) DO UPDATE SET
  host_doc_id = EXCLUDED.host_doc_id,
  link_doc_id = EXCLUDED.link_doc_id,
  origin_x = EXCLUDED.origin_x,
  origin_y = EXCLUDED.origin_y,
  origin_z = EXCLUDED.origin_z,
  basisx_x = EXCLUDED.basisx_x,
  basisx_y = EXCLUDED.basisx_y,
  basisx_z = EXCLUDED.basisx_z,
  basisy_x = EXCLUDED.basisy_x,
  basisy_y = EXCLUDED.basisy_y,
  basisy_z = EXCLUDED.basisy_z,
  basisz_x = EXCLUDED.basisz_x,
  basisz_y = EXCLUDED.basisz_y,
  basisz_z = EXCLUDED.basisz_z,
  rotation_z_radians = EXCLUDED.rotation_z_radians,
  angle_to_true_north_radians = EXCLUDED.angle_to_true_north_radians,
  last_saved = EXCLUDED.last_saved";

        try
        {
            if (conn == null)
                ExecuteNonQuery(sqlV2, args);
            else
                ExecuteNonQuery(conn, tx, sqlV2, args);
        }
        catch (PostgresException)
        {
            if (conn == null)
                ExecuteNonQuery(sqlV1, args);
            else
                ExecuteNonQuery(conn, tx, sqlV1, args);
        }
    }

    public void UpsertLinkedElement(
        string hostDocId,
        int linkInstanceId,
        string linkDocId,
        int id,
        Guid guid,
        string name,
        string category,
        string typeName,
        string level,
        DateTime lastSaved,
        NpgsqlConnection conn = null,
        NpgsqlTransaction tx = null)
    {
        string sql = @"CREATE TABLE IF NOT EXISTS revit_linked_elements (
  host_doc_id TEXT NOT NULL,
  link_instance_id INTEGER NOT NULL,
  link_doc_id TEXT NOT NULL,
  id INTEGER NOT NULL,
  guid UUID,
  name TEXT,
  category TEXT,
  type_name TEXT,
  level TEXT,
  last_saved TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT pk_revit_linked_elements PRIMARY KEY (host_doc_id, link_instance_id, id)
);
INSERT INTO revit_linked_elements (
  host_doc_id, link_instance_id, link_doc_id, id, guid, name, category, type_name, level, last_saved)
VALUES (
  @host, @iid, @link, @id, @guid, @name, @cat, @type, @level, @ts)
ON CONFLICT (host_doc_id, link_instance_id, id) DO UPDATE SET
  link_doc_id = EXCLUDED.link_doc_id,
  guid = EXCLUDED.guid,
  name = EXCLUDED.name,
  category = EXCLUDED.category,
  type_name = EXCLUDED.type_name,
  level = EXCLUDED.level,
  last_saved = EXCLUDED.last_saved
  WHERE EXCLUDED.last_saved > revit_linked_elements.last_saved";

        var args = new[]
        {
            new NpgsqlParameter("@host", hostDocId ?? (object)DBNull.Value),
            new NpgsqlParameter("@iid", linkInstanceId),
            new NpgsqlParameter("@link", linkDocId ?? (object)DBNull.Value),
            new NpgsqlParameter("@id", id),
            new NpgsqlParameter("@guid", guid),
            new NpgsqlParameter("@name", name ?? (object)DBNull.Value),
            new NpgsqlParameter("@cat", category ?? (object)DBNull.Value),
            new NpgsqlParameter("@type", typeName ?? (object)DBNull.Value),
            new NpgsqlParameter("@level", level ?? (object)DBNull.Value),
            new NpgsqlParameter("@ts", lastSaved)
        };

        if (conn == null)
            ExecuteNonQuery(sql, args);
        else
            ExecuteNonQuery(conn, tx, sql, args);
    }

    public void UpsertModelInfo(string docId, string modelName, Guid guid, DateTime lastSaved, string projectInfo = null, string projectParameters = null,
        NpgsqlConnection conn = null, NpgsqlTransaction tx = null)
    {
        string sql = @"INSERT INTO model_info
            (doc_id, model_name, guid, last_saved, project_info, project_parameters)
            VALUES (@doc, @name, @guid, @last_saved, @info, @params)
            ON CONFLICT (doc_id) DO UPDATE SET
                model_name = EXCLUDED.model_name,
                guid = EXCLUDED.guid,
                last_saved = EXCLUDED.last_saved,
                project_info = EXCLUDED.project_info,
                project_parameters = EXCLUDED.project_parameters
            WHERE EXCLUDED.last_saved > model_info.last_saved";

        var infoParam = new NpgsqlParameter("@info", projectInfo ?? (object)DBNull.Value)
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb
        };
        var paramsParam = new NpgsqlParameter("@params", projectParameters ?? (object)DBNull.Value)
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb
        };

        var args = new List<NpgsqlParameter>
        {
            new NpgsqlParameter("@doc", docId ?? (object)DBNull.Value),
            new NpgsqlParameter("@name", modelName ?? (object)DBNull.Value),
            new NpgsqlParameter("@guid", guid),
            new NpgsqlParameter("@last_saved", lastSaved),
            infoParam,
            paramsParam
        };

        if (conn == null)
            ExecuteNonQuery(sql, args.ToArray());
        else
            ExecuteNonQuery(conn, tx, sql, args.ToArray());
    }

    public void UpsertLinkedModelInfo(string hostDocId, string linkDocId, string modelName, Guid guid, DateTime lastSaved, string projectInfo = null, string projectParameters = null,
        NpgsqlConnection conn = null, NpgsqlTransaction tx = null)
    {
        string sql = @"CREATE TABLE IF NOT EXISTS model_info_linked (
  host_doc_id TEXT NOT NULL,
  link_doc_id TEXT NOT NULL,
  model_name TEXT,
  guid UUID,
  last_saved TIMESTAMP,
  project_info JSONB,
  project_parameters JSONB,
  CONSTRAINT pk_model_info_linked PRIMARY KEY (host_doc_id, link_doc_id)
);
INSERT INTO model_info_linked (host_doc_id, link_doc_id, model_name, guid, last_saved, project_info, project_parameters)
VALUES (@host, @link, @name, @guid, @last_saved, @info, @params)
ON CONFLICT (host_doc_id, link_doc_id) DO UPDATE SET
  model_name = EXCLUDED.model_name,
  guid = EXCLUDED.guid,
  last_saved = EXCLUDED.last_saved,
  project_info = EXCLUDED.project_info,
  project_parameters = EXCLUDED.project_parameters
WHERE EXCLUDED.last_saved > model_info_linked.last_saved";

        var infoParam = new NpgsqlParameter("@info", (object)projectInfo ?? DBNull.Value) { NpgsqlDbType = NpgsqlDbType.Jsonb };
        var paramsParam = new NpgsqlParameter("@params", (object)projectParameters ?? DBNull.Value) { NpgsqlDbType = NpgsqlDbType.Jsonb };

        var args = new List<NpgsqlParameter>
        {
            new NpgsqlParameter("@host", hostDocId ?? (object)DBNull.Value),
            new NpgsqlParameter("@link", linkDocId ?? (object)DBNull.Value),
            new NpgsqlParameter("@name", modelName ?? (object)DBNull.Value),
            new NpgsqlParameter("@guid", guid),
            new NpgsqlParameter("@last_saved", lastSaved),
            infoParam,
            paramsParam
        };

        if (conn == null)
            ExecuteNonQuery(sql, args.ToArray());
        else
            ExecuteNonQuery(conn, tx, sql, args.ToArray());
    }

    public void UpsertLinkedElementType(
        string hostDocId,
        int linkInstanceId,
        string linkDocId,
        int id,
        Guid guid,
        string family,
        string typeName,
        string category,
        DateTime lastSaved,
        NpgsqlConnection conn = null,
        NpgsqlTransaction tx = null)
    {
        string sql = @"CREATE TABLE IF NOT EXISTS revit_linked_elementtypes (
  host_doc_id TEXT NOT NULL,
  link_instance_id INTEGER NOT NULL,
  link_doc_id TEXT NOT NULL,
  id INTEGER NOT NULL,
  guid UUID,
  family TEXT,
  type_name TEXT,
  category TEXT,
  last_saved TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT pk_revit_linked_elementtypes PRIMARY KEY (host_doc_id, link_instance_id, id)
);
INSERT INTO revit_linked_elementtypes (
  host_doc_id, link_instance_id, link_doc_id, id, guid, family, type_name, category, last_saved)
VALUES (
  @host, @iid, @link, @id, @guid, @family, @type, @cat, @ts)
ON CONFLICT (host_doc_id, link_instance_id, id) DO UPDATE SET
  link_doc_id = EXCLUDED.link_doc_id,
  guid = EXCLUDED.guid,
  family = EXCLUDED.family,
  type_name = EXCLUDED.type_name,
  category = EXCLUDED.category,
  last_saved = EXCLUDED.last_saved
WHERE EXCLUDED.last_saved > revit_linked_elementtypes.last_saved";

        var args = new[]
        {
            new NpgsqlParameter("@host", hostDocId ?? (object)DBNull.Value),
            new NpgsqlParameter("@iid", linkInstanceId),
            new NpgsqlParameter("@link", linkDocId ?? (object)DBNull.Value),
            new NpgsqlParameter("@id", id),
            new NpgsqlParameter("@guid", guid),
            new NpgsqlParameter("@family", family ?? (object)DBNull.Value),
            new NpgsqlParameter("@type", typeName ?? (object)DBNull.Value),
            new NpgsqlParameter("@cat", category ?? (object)DBNull.Value),
            new NpgsqlParameter("@ts", lastSaved)
        };

        if (conn == null)
            ExecuteNonQuery(sql, args);
        else
            ExecuteNonQuery(conn, tx, sql, args);
    }

    public DateTime? GetModelLastSaved(string docId)
    {
        var rows = Query("SELECT last_saved FROM model_info WHERE doc_id = @doc", new NpgsqlParameter("@doc", docId));
        if (rows.Count > 0 && rows[0].ContainsKey("last_saved") && rows[0]["last_saved"] is DateTime dt)
            return dt;
        return null;
    }

    // Queue helpers ------------------------------------------------------
    public int EnqueuePlan(string planJson)
    {
        string sql = "INSERT INTO mcp_queue (plan) VALUES (@plan) RETURNING id";
        using (var conn = new NpgsqlConnection(_connectionString))
        using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter("@plan", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = planJson });
            conn.Open();
            return (int)cmd.ExecuteScalar();
        }
    }

    public (int id, string plan) DequeuePlan()
    {
        // UPDATE instead of DELETE so the row survives for SetJobResult and error tracking
        string sql = @"UPDATE mcp_queue SET status = 'processing'
            WHERE id = (
                SELECT id FROM mcp_queue
                WHERE status = 'pending'
                ORDER BY id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            ) RETURNING id, plan::text";
        using (var conn = new NpgsqlConnection(_connectionString))
        using (var cmd = new NpgsqlCommand(sql, conn))
        {
            conn.Open();
            using (var reader = cmd.ExecuteReader())
            {
                if (reader.Read())
                {
                    int id = reader.GetInt32(0);
                    string plan = reader.GetString(1);
                    return (id, plan);
                }
            }
        }
        return (0, null);
    }

    public void SetJobResult(int id, string status, string resultJson)
    {
        string sql = "UPDATE mcp_queue SET status=@s, completed_at=NOW(), result=@r WHERE id=@id";
        ExecuteNonQuery(sql,
            new NpgsqlParameter("@s", status),
            new NpgsqlParameter("@r", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = (object)resultJson ?? DBNull.Value },
            new NpgsqlParameter("@id", id));
    }
}
