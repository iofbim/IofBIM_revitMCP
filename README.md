# Revit MCP Plugin

This project provides a Revit add‑in that exposes a Model Context Protocol (MCP) server to automate Revit via simple HTTP JSON requests. Commands are strongly typed and executed on Revit’s UI thread via External Events. Optionally, the add‑in synchronizes model data to PostgreSQL so an AI agent can search and reason over the model. A companion n8n workflow (in `AIAgentWorkflows/`) connects a chat model to MCP and includes vector search tools backed by PGVector.

---

## Features

- HTTP listener: serves `http://localhost:5005/mcp/` for JSON commands.
- Command pattern: strongly‑typed `ICommand` implementations in `Commands/*`.
- Safe UI dispatch: uses Revit `ExternalEvent` to run on the UI thread.
- View filter automation: create/apply graphical filters with overrides.
- Database sync + SQL: export Revit model data to PostgreSQL; run queries.
- Vector search tools (via n8n): category/API disambiguation with PGVector.
- Plan execution: chain multiple commands in a single request.
- Async job queue: enqueue long‑running plans; poll DB for completion.
- In‑memory cache: cache per‑session metadata (e.g., views, bindings).

---

## Project Structure

| Area                    | Purpose                                                                 |
| ----------------------- | ----------------------------------------------------------------------- |
| `Core/*`                | MCP HTTP server, routing, queue processor, app entry.                   |
| `Commands/*`            | Strongly‑typed Revit commands (list, modify, sync, SQL, plans).         |
| `Helpers/*`             | Utilities for Revit, UI, categories, caching, DB config.                |
| `Data/*`                | PostgreSQL helpers, batching, and job queue helpers.                    |
| `postgres_schema.sql`   | Tables for model data, parameters, views, jobs, etc.                    |
| `AIAgentWorkflows/*`    | n8n workflow with vector tools (PGVector) and chat orchestration.       |

---

## Dashboard (optional)

- Open `dashboard/index.html` in a browser to see quick charts (elements by category/level, types by category, top parameters, views by type).
- The MCP server now includes CORS headers and handles `OPTIONS` preflight so the page can call `Db.Query` directly from the browser.
- You can embed Flowise or n8n chat inside the dashboard; see `dashboard/README.md` for details.

---

## How It Works

- Revit add‑in loads an HTTP listener (`Core/McpServer.cs`) that accepts JSON payloads at `.../mcp/` and routes them to `ICommand` handlers (`Core/RequestHandler.cs`).
- Commands use the Revit API to list, query, filter, or modify the model. Some commands sync model metadata and parameters to tables defined in `postgres_schema.sql`.
- An optional background worker (`Core/QueueProcessor.cs`) executes queued plans stored in the `mcp_queue` table.
- The n8n workflow connects a chat model (Ollama or OpenAI) to tools:
  - `RevitBuiltinCategories`: PGVector store mapping natural names to `BuiltInCategory` values (table `revit_builtin_categories`).
  - `RevitApiVectorDB`: PGVector store for Revit API text chunks (table `revit_api_chunks`).
  - `Communicator`: forwards structured JSON commands to MCP.
  - `SQL Querier`: queries the PostgreSQL model tables.

---

## Setup

- Revit add‑in
  - Build `IofBIM_revitMCP` for your Revit version (project targets Revit 2026 DLLs, requires .NET 8).
  - Deploy the compiled DLLs into your Revit Addins folder and create a `.addin` manifest pointing to `IofBIM_revitMCP.dll`.
  - Example manifest (save as `IofBIM_revitMCP.addin` under `%AppData%\Autodesk\Revit\Addins\2025`):

  ```xml
  <?xml version="1.0" encoding="utf-8"?>
  <RevitAddIns>
    <AddIn Type="Application">
      <Name>IofBIM Revit MCP</Name>
      <Assembly>FULL_PATH_TO\IofBIM_revitMCP.dll</Assembly>
      <AddInId>8D83BE14-B739-4ACD-A9DB-7FD3F674B80B</AddInId>
      <FullClassName>App</FullClassName>
      <VendorId>IofBIM</VendorId>
      <VendorDescription>MCP server for Revit</VendorDescription>
    </AddIn>
  </RevitAddIns>
  ```

- Database (PostgreSQL)
  - Install PostgreSQL and create a database; run `postgres_schema.sql` to create tables.
  - Install the `pgvector` extension if you want vector search in n8n: `CREATE EXTENSION IF NOT EXISTS vector;`.
  - Provide a connection string to the add‑in using one of:
    - App.config `<connectionStrings>` key `revit` (not recommended for secrets in source control).
    - Environment variable `REVIT_DB_CONN`.
    - A file path provided in requests via `"conn_file"`.
    - Fallback `revit-conn.txt` placed next to the plugin DLL.

---

## Secrets Management

- Preferred options (in the order the add‑in checks):
  1) App.config: `connectionStrings["revit"]` (left empty by default in this repo).
  2) Environment variable: `REVIT_DB_CONN`.
     - PowerShell (current user):

       ```powerShell
       [System.Environment]::SetEnvironmentVariable(
         "REVIT_DB_CONN",
         "Host=localhost;Port=5432;Database=...;Username=...;Password=...;SSL Mode=Disable;",
         "User"
       )
       ```

  3) Request input: pass `"conn_file": "C:\\path\\to\\revit-conn.txt"` in the JSON payload.
  4) Fallback file: place `revit-conn.txt` next to the add‑in DLL (git‑ignored).

- This repo ignores `App.config` and `revit-conn.txt`. Use `App.config.example` as a template if you need to create a local `App.config`.

- n8n workflow (vector tools + orchestration)
  - Import `AIAgentWorkflows/IofBIM_Revit_MCP_Controller_Local_LLM.json` into n8n.
  - Configure credentials:
    - PostgreSQL: points to the same DB used by the add‑in; used by PGVector nodes and chat memory (table `IofBIM_revitMCP_chatHistory`).
    - LLM: either Ollama (e.g., `llama3.2:latest`) or OpenAI.
  - Ensure the two PGVector stores are available (as configured in the workflow):
    - `revit_builtin_categories`: embeddings for category names/aliases.
    - `revit_api_chunks`: embeddings for Revit API text chunks.
  - Seed vectors as needed (see `AIAgentWorkflows/README.md`).

---

## Security Notes

- The listener currently binds to `http://*:5005/mcp/` in code; for workstation use, prefer restricting to loopback `http://localhost:5005/mcp/` and add a simple auth token header at the MCP edge (reverse proxy) or in code.
- `Db.Query` executes arbitrary SQL; protect access or disable in untrusted environments.
- Avoid committing credentials; prefer `REVIT_DB_CONN` or local `revit-conn.txt` (git‑ignored).

---

## Commands (high level)

- Listing: `Elements.List`, `Parameters.ListForElements`, `Types.List`, `Views.List`, `Sheets.List`, `Schedules.List`, `Model.GetContext`.
- Modifying: `Elements.Modify`, `Parameters.CreateShared`, `Sheets.Create`, `Views.PlaceOnSheet`, `Filters.AddToView`.
- Data: `Db.SyncModel` (optionally `"async":"true"`), `Db.Query`.
- Orchestration: `Plan.Execute`, `Plan.Enqueue` (background via DB queue).

See `Commands/*` for request fields and `AIAgentWorkflows/*` for examples embedded in the n8n tool descriptions.

---

## Troubleshooting

- If DB connectivity fails, check `C:\\Temp\\pg-debug.txt` (written by `Db.SyncModel`).
- Logs: `mcp.log` (HTTP) and `revit-export-error.log` (export issues).
- If vector tools appear empty, make sure `pgvector` is installed and the embedding tables are seeded.

---

## License

See `LICENSE`.

---

## Example Payloads

- Db.SyncModel:
  - Action: `Db.SyncModel`
  - Notes: accepts optional `"async":"true"`, `"sync_links":"true"`, and `"conn_file"`.

  ```json
  {
    "action": "Db.SyncModel",
    "async": "false",
    "sync_links": "true",
    "conn_file": "C:\\Temp\\revit-conn.txt"
  }
  ```

- Db.Query:
  - Action: `Db.Query`
  - Notes: pass SQL in `sql`, optional parameters JSON in `params`.

  ```json
  {
    "action": "Db.Query",
    "sql": "SELECT id, name, category FROM revit_elements WHERE doc_id=@doc LIMIT 5",
    "params": {"@doc": "C:\\Projects\\Sample.rvt"}
  }
  ```

- Views.List (writes to DB when connection present):

  ```json
  {"action": "Views.List", "include_linked": "false"}
  ```

- ListCategories:

  ```json
  {"action": "ListCategories", "include_linked": "false"}
  ```

> Tip: If you don�t want to store credentials in `App.config`, set `REVIT_DB_CONN` or provide `conn_file` pointing to a local text file with the connection string.

---

## Schema Notes

- The bundled `postgres_schema.sql` uses composite keys that include `doc_id` (or `host_doc_id` with `link_instance_id`) to support multiple Revit models and multiple link instances in the same database.
- Existing databases created with earlier versions (single-key tables) still work: the code attempts a doc-aware upsert first and falls back to legacy constraints if needed.
- For new installs, run `postgres_schema.sql` to get the doc-aware schema.
- Linked data (when `Db.SyncModel` is called with `"include_links":"true"`):
  - `revit_link_instances(host_doc_id, instance_id, ...)`: one row per link instance in the host.
  - `revit_linked_elements(host_doc_id, link_instance_id, id, ...)`: elements per link instance (batched).
  - `revit_linked_parameters(host_doc_id, link_instance_id, element_id, param_name, is_type, ...)`: element params and type params (`is_type=true`).
  - `revit_linked_elementtypes(host_doc_id, link_instance_id, id, ...)`: linked element type metadata (batched).
  - `model_info_linked(host_doc_id, link_doc_id, ...)`: per-host model info for the linked document.
- Stale-row pruning: during sync, rows older than the current session `last_saved` are pruned for the active host model and processed link instances.

## For MCP server to run

in powershell with administrator rights exebute below code

```powershell

netsh http add urlacl url=http://*:5005/mcp/ user=Everyone

```