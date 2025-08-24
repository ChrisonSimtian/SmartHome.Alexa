## High‑level flow

The app mirrors your Python script in four steps:

1. **Get entities** → save raw JSON to `data.json`
2. **Delete entities** whose `description` contains `"Home Assistant"`
3. **Get GraphQL endpoints** → save raw JSON to `graphql.json`
4. **Delete endpoints** whose `legacyAppliance.manufacturerName` contains `"Home Assistant"`

If anything fails deletion, it prints a summary.

```
Program.Main
 ├─ GetEntitiesAsync      --> writes data.json
 ├─ DeleteEntitiesAsync   --> reads data.json, deletes, verifies via CheckDeviceDeletedAsync
 ├─ GetGraphqlEndpointsAsync --> writes graphql.json
 ├─ DeleteEndpointsAsync     --> reads graphql.json, deletes, verifies
 └─ summary output
```

---

## File layout (single file for simplicity)

Everything is in `Program.cs` under the namespace `AlexaCleaner`. You can split models/utilities into separate files later.

- `Program` class (entry point + orchestration)
- **HTTP helpers** (header setup, deviceId builder)
- **Step functions**:
  - `GetEntitiesAsync()`
  - `DeleteEntitiesAsync()`
  - `GetGraphqlEndpointsAsync()`
  - `DeleteEndpointsAsync()`
  - `CheckDeviceDeletedAsync()` (verification)
- **Models** for the JSON you read/write
- **JSON utility**: `JsonExtensions.GetPropertyOrDefault` (tolerant parsing)

---

## Key parts explained

### 1) Entry point & orchestration

```csharp
private static async Task<int> Main()
{
    _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    var entities = await GetEntitiesAsync();
    var failedEntities = await DeleteEntitiesAsync();

    await GetGraphqlEndpointsAsync();
    var failedEndpoints = await DeleteEndpointsAsync();

    // print summary
}
```

- Creates a single `HttpClient` for the run.
- Runs the four steps in order.
- Prints a summary of failures if any.

**Why this way?**  
It keeps a clean, linear pipeline and mirrors your Python script so behavior remains familiar.

---

### 2) Settings & constants

At the top of `Program` you’ll see:

- **Toggles**: `DEBUG`, `SHOULD_SLEEP`
- **Filter**: `DESCRIPTION_FILTER_TEXT`
- **Auth/context**: `COOKIE`, `X_AMZN_ALEXA_APP`, `CSRF`, `ROUTINE_VERSION`, `USER_AGENT`, `DELETE_SKILL`, `HOST`
- **Files**: `DATA_FILE`, `GRAPHQL_FILE`
- **URLs**: `GET_URL`, `DELETE_URL_PREFIX`, `GRAPHQL_URL`

> In production, you’d want these in `appsettings.json` or env vars and avoid hard‑coding secrets.

---

### 3) JSON serialization options

```csharp
private static readonly JsonSerializerOptions JsonOpts = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};
```

- Applies to outgoing payloads and any strongly‑typed deserialization.
- Raw bodies are still saved 1:1 to the files to preserve parity with Python.

---

### 4) HTTP plumbing

- Uses **`HttpClient`** and `HttpRequestMessage` to explicitly set headers per request.
- A small helper `AddCommonHeaders()` sets general connection properties; sensitive/managed headers like `Host`, `Content-Length`, `Connection` are left to the runtime (they’re restricted in .NET).
- **Request ID**: regenerated with `Guid.NewGuid()` for each request that needs `x-amzn-RequestId`.

---

### 5) Step: Get entities

```csharp
private static async Task<List<Entity>> GetEntitiesAsync()
```

- **GET** to `GET_URL` with required headers.
- Saves the raw body to `data.json`.
- Tries to strongly deserialize to `List<Entity>`. If the shape doesn’t match, falls back to a tolerant parse using `JsonDocument` and `GetPropertyOrDefault`.

**Model used:**
```csharp
internal sealed class Entity
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
}
```

Only includes fields you actually use later.

---

### 6) Step: Delete entities

```csharp
private static async Task<List<FailureInfo>> DeleteEntitiesAsync()
```

- Reads `data.json`, deserializes to `List<Entity>`.
- Filters where `Description` contains `"Home Assistant"` (case‑insensitive).
- **URL device ID**: creates `deviceIdForUrl` from `description` by:
  - `.` → `%23`
  - remove `" via Home Assistant"`
  - lower‑case  
  (exactly like your Python transformation)

- Sends **DELETE** to `${DELETE_URL_PREFIX}${deviceIdForUrl}`.
- After each attempt, calls `CheckDeviceDeletedAsync(entityId)` to verify the resource is gone (expects `404`).
- Behavior matches the Python script: although the loop is “up to 4 tries,” it **breaks after the first failed verify**, preserving your original control flow.

Failures are collected as:

```csharp
internal sealed class FailureInfo
{
    public string Name { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Description { get; set; } = "";
}
```

---

### 7) Step: Get GraphQL endpoints

```csharp
private static async Task GetGraphqlEndpointsAsync()
```

- **POST** to `GRAPHQL_URL` with a GraphQL query (same fields you used in Python).
- Saves raw response to `graphql.json`.  
- No strict parsing here; the delete step will parse what it needs.

Payload model:

```csharp
internal sealed class GraphQlQuery
{
    public string Query { get; set; } = "";
}
```

---

### 8) Step: Delete endpoints

```csharp
private static async Task<List<FailureInfo>> DeleteEndpointsAsync()
```

- Reads `graphql.json` and deserializes into light models that reflect only what you need:

```csharp
internal sealed class GraphQlResponse { public GraphQlData? Data { get; set; } }
internal sealed class GraphQlData { public GraphQlEndpoints? Endpoints { get; set; } }
internal sealed class GraphQlEndpoints { public List<EndpointItem>? Items { get; set; } }
internal sealed class EndpointItem
{
    public string? FriendlyName { get; set; }
    public LegacyAppliance? LegacyAppliance { get; set; }
}
internal sealed class LegacyAppliance
{
    public string? ApplianceKey { get; set; }
    public string? FriendlyDescription { get; set; }
    public string? ManufacturerName { get; set; }
}
```

- Filters items where `legacyAppliance.manufacturerName` contains `"Home Assistant"`.
- For each match:
  - derives the **same** `deviceIdForUrl` from `friendlyDescription`
  - **DELETE** the endpoint, then verify with `CheckDeviceDeletedAsync(applianceKey)` (404 check).
- Collects failures and prints them.

---

### 9) Verification helper

```csharp
private static async Task<bool> CheckDeviceDeletedAsync(string entityId)
```

- **GET** `https://{HOST}/api/smarthome/v1/presentation/devices/control/{entityId}`
- Returns `true` if the status is **404**, which your Python code treats as “deleted”.

---

### 10) Error handling & logging

- `Main` wraps the pipeline in a `try/catch` with:
  - `TaskCanceledException` (timeout) handling
  - generic `Exception` handling
- `DEBUG` flag dumps response codes and bodies where helpful.
- `SHOULD_SLEEP` adds a 200ms delay between deletes to reduce pressure on the API.

---

## How it maps to your Python script

| Python Function           | C# Method                          | Notes |
|--------------------------|-------------------------------------|-------|
| `get_entities()`         | `GetEntitiesAsync()`                | Saves `data.json` and returns a list (or tolerant parse). |
| `delete_entities()`      | `DeleteEntitiesAsync()`             | Same filter & URL transformation; verify via GET(404). |
| `get_graphql_endpoints()`| `GetGraphqlEndpointsAsync()`        | Posts the identical GraphQL query; saves `graphql.json`. |
| `delete_endpoints()`     | `DeleteEndpointsAsync()`            | Same manufacturer filter; same URL transformation & verify. |
| `check_device_deleted()` | `CheckDeviceDeletedAsync()`         | GET on device page; 404 means deleted. |

Behavioral details preserved:
- Up to 4 attempts but break after first failed verify (as in your Python).
- Case‑insensitive filter matching.
- Device ID string transformation.

---

## Where you might take it next (optional refactor ideas)

- **Configuration**: move secrets and constants to `appsettings.json` + `IOptions<T>`, read env overrides for CI/CD.
- **Logging**: use `Microsoft.Extensions.Logging` instead of `Console.WriteLine`, add structured logs (request ids, entity ids).
- **Resilience**: add backoff/retry only for transient HTTP statuses; preserve the explicit verify step.
- **DI/Composition**: split into services (`AlexaClient`, `EntityCleaner`, `EndpointCleaner`) for unit testing.
- **Typed models**: if the JSON schemas are stable, promote tolerant parsing to strongly typed models everywhere.
- **Rate limiting**: add a simple `SemaphoreSlim` or Polly rate limit if you see throttling.
- **Secrets**: load `COOKIE`, `CSRF`, `X_AMZN_ALEXA_APP` from a secure store (Key Vault, 1Password, etc.).

---

If you want, I can refactor this into a DI‑driven app with `appsettings.json` and `ILogger<T>` so you can run it as an Azure Container Instance or a scheduled job. Do you plan to run this one‑off, or repeatedly as part of a cleanup task?
