# PostgreSQL Integration Tasks

## 1. Setup Npgsql Data Layer

- Add `Npgsql` (and optionally `Dapper`) package references in `IofBIM_revitMCP.csproj` and `packages.config`.
- Create a new folder `Data` with `PostgresDb.cs` that manages a connection string and exposes helpers:
  - `ExecuteNonQuery(string sql, params NpgsqlParameter[] args)`
  - `Query(string sql, params NpgsqlParameter[] args) : List<Dictionary<string,object>>`
  - Upsert helpers for each table: `UpsertElement`, `UpsertParameter`, `UpsertCategory`, `UpsertView`, `UpsertSheet`, `UpsertSchedule`, `UpsertFamily`.
- Use `System.Configuration.ConfigurationManager` to read the connection string from `App.config`.

## 2. Sync Commands

- **SyncModelToSqlCommand.cs**
  - Accepts optional `element_ids` to limit export.
  - Gathers element metadata (id, guid, name, category, type name, level, doc id).
  - Writes/updates records in `revit_elements` and `revit_parameters` via `PostgresDb`.
  - Removes DB records whose `last_saved` is older than current session for that document.
  - Returns counts of inserted/updated/deleted records.
- **ListCategoriesCommand.cs**
  - Enumerates all categories, populates `revit_categories` table.
- **ListViewsCommand.cs**, **ListSheetsCommand.cs**, **ListSchedulesCommand.cs**
  - Export corresponding metadata (view type, sheet number, etc.) and sync to their tables.
- Extend **GetFamiliesAndTypesCommand.cs** to capture family GUID and doc id and update `revit_families`.

## 3. Query Commands

- **QuerySqlCommand.cs**
  - Accepts a SQL string and optional parameters.
  - Executes the query through `PostgresDb` and returns results as a list of dictionaries.
- **GetElementMetadataCommand.cs** (optional convenience)
  - Fetches metadata for specified element ids directly from the database rather than Revit.

## 4. Command Registration

- Update `Core/RequestHandler.cs` `CommandMap` with the new commands: `SyncModelToSql`, `QuerySql`, `ListCategories`, `ListViews`, `ListSheets`, `ListSchedules` and updated `GetFamiliesAndTypes`.

## 5. Documentation

- Document connection string configuration in `README.md` with an example `App.config` snippet.
- Provide example JSON payloads for `SyncModelToSql` and `QuerySql` commands.

These tasks will create a PostgreSQL-backed knowledge graph of the Revit model, keeping the tables synchronized and allowing the AI agent to issue SQL queries over HTTP.
