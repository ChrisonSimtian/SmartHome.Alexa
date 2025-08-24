// Program.cs
// .NET 7/8 Console app
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AlexaCleaner
{
    internal static class Program
    {
        // ===== Settings (edit these to match your setup) =====
        private const bool DEBUG = false;                 // set to true for verbose logs
        private const bool SHOULD_SLEEP = false;          // add a tiny delay between deletes
        private const string DESCRIPTION_FILTER_TEXT = "Home Assistant";

        private const string HOST = "na-api-alexa.amazon.ca";
        private const string USER_AGENT = "AppleWebKit PitanguiBridge/2.2.635412.0-[HARDWARE=iPhone17_3][SOFTWARE=18.2][DEVICE=iPhone]";
        private const string ROUTINE_VERSION = "3.0.255246";
        private const string COOKIE =
            ";at-acbca=\"LONG STRING\";sess-at-acbca=\"SHORT STRING\";session-id=000-0000000-0000000;session-id-time=2366612930l;session-token=LONG_STRING;ubid-acbca=000-0000000-00000;x-acbca=\"SHORT_STRING\";csrf=NUMBER";
        private const string X_AMZN_ALEXA_APP = "LONG_STRING";
        private const string CSRF = "NUMBER"; // must match the cookie's csrf value
        private const string DELETE_SKILL = "SKILL_LONG_STRING";

        // ===== Constants =====
        private const string DATA_FILE = "data.json";
        private const string GRAPHQL_FILE = "graphql.json";
        private static readonly string GET_URL = $"https://{HOST}/api/behaviors/entities?skillId=amzn1.ask.1p.smarthome";
        private static readonly string DELETE_URL_PREFIX = $"https://{HOST}/api/phoenix/appliance/{DELETE_SKILL}%3D%3D_";
        private static readonly string GRAPHQL_URL = $"https://{HOST}/nexus/v1/graphql";

        // ===== Json options =====
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static HttpClient _http = null!;

        private static async Task<int> Main()
        {
            // HttpClient with a reasonable timeout
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            try
            {
                var entities = await GetEntitiesAsync();
                var failedEntities = await DeleteEntitiesAsync();

                await GetGraphqlEndpointsAsync();
                var failedEndpoints = await DeleteEndpointsAsync();

                if (failedEntities.Any() || failedEndpoints.Any())
                {
                    Console.WriteLine("\nSummary of all failed deletions:");

                    if (failedEntities.Any())
                    {
                        Console.WriteLine("\nFailed Entities:");
                        foreach (var f in failedEntities)
                        {
                            Console.WriteLine($"Name: '{f.Name}', Entity ID: '{f.EntityId}'");
                        }
                    }

                    if (failedEndpoints.Any())
                    {
                        Console.WriteLine("\nFailed Endpoints:");
                        foreach (var f in failedEndpoints)
                        {
                            Console.WriteLine($"Name: '{f.Name}', Entity ID: '{f.EntityId}'");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"Done, removed all entities and endpoints with a manufacturer/name matching: {DESCRIPTION_FILTER_TEXT}");
                }

                return 0;
            }
            catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Request timed out: {ex.Message}");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        // ======= Step 1: Get Entities =======
        private static async Task<List<Entity>> GetEntitiesAsync()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, GET_URL);
            AddCommonHeaders(req);
            req.Headers.TryAddWithoutValidation("Routines-Version", ROUTINE_VERSION);
            req.Headers.TryAddWithoutValidation("Cookie", COOKIE);
            req.Headers.TryAddWithoutValidation("x-amzn-alexa-app", X_AMZN_ALEXA_APP);
            req.Headers.TryAddWithoutValidation("Accept", "application/json; charset=utf-8");
            req.Headers.TryAddWithoutValidation("User-Agent", USER_AGENT);

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var body = await res.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(body))
            {
                Console.WriteLine("Empty response received from server.");
                return new();
            }

            // Persist the raw JSON (just like the Python script)
            await File.WriteAllTextAsync(DATA_FILE, body, Encoding.UTF8);

            // For the delete step we only need a few fields; attempt to deserialize a minimal list.
            // If the shape is different, we fallback to a tolerant parse.
            try
            {
                var entities = JsonSerializer.Deserialize<List<Entity>>(body, JsonOpts) ?? new();
                return entities;
            }
            catch
            {
                // Tolerant fallback: try to extract known fields dynamically
                using var doc = JsonDocument.Parse(body);
                var result = new List<Entity>();
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        result.Add(new Entity
                        {
                            Id = el.GetPropertyOrDefault("id"),
                            DisplayName = el.GetPropertyOrDefault("displayName"),
                            Description = el.GetPropertyOrDefault("description")
                        });
                    }
                }
                return result;
            }
        }

        // ======= Utility: Check device deleted =======
        private static async Task<bool> CheckDeviceDeletedAsync(string entityId)
        {
            var url = $"https://{HOST}/api/smarthome/v1/presentation/devices/control/{Uri.EscapeDataString(entityId ?? string.Empty)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddCommonHeaders(req);
            req.Headers.TryAddWithoutValidation("x-amzn-RequestId", Guid.NewGuid().ToString());
            req.Headers.TryAddWithoutValidation("Accept", "application/json; charset=utf-8");
            req.Headers.TryAddWithoutValidation("User-Agent", USER_AGENT);
            req.Headers.TryAddWithoutValidation("Cookie", COOKIE);
            req.Headers.TryAddWithoutValidation("x-amzn-alexa-app", X_AMZN_ALEXA_APP);

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (DEBUG)
            {
                var text = await res.Content.ReadAsStringAsync();
                Console.WriteLine($"Check device deleted status: {(int)res.StatusCode}");
                Console.WriteLine($"Check device deleted body: {text}");
            }
            return res.StatusCode == HttpStatusCode.NotFound; // 404 means gone
        }

        // ======= Step 2: Delete Entities =======
        private static async Task<List<FailureInfo>> DeleteEntitiesAsync()
        {
            var failed = new List<FailureInfo>();

            // Read entities from the saved file (matches the Python script’s flow)
            if (!File.Exists(DATA_FILE))
                return failed;

            var json = await File.ReadAllTextAsync(DATA_FILE, Encoding.UTF8);
            var entities = DeserializeListSafe<Entity>(json);

            foreach (var item in entities)
            {
                var description = item.Description ?? string.Empty;
                if (!description.Contains(DESCRIPTION_FILTER_TEXT, StringComparison.OrdinalIgnoreCase))
                    continue;

                var entityId = item.Id ?? string.Empty;
                var name = item.DisplayName ?? string.Empty;
                var deviceIdForUrl = BuildDeviceIdForUrl(description);

                Console.WriteLine($"Name: '{name}', Entity ID: '{entityId}', Device ID: '{deviceIdForUrl}', Description: '{description}'");

                var url = $"{DELETE_URL_PREFIX}{deviceIdForUrl}";
                var deletionSuccess = false;

                for (var attempt = 0; attempt < 4; attempt++)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                    AddCommonHeaders(req);
                    req.Headers.TryAddWithoutValidation("x-amzn-alexa-app", X_AMZN_ALEXA_APP);
                    req.Headers.TryAddWithoutValidation("Accept", "application/json; charset=utf-8");
                    req.Headers.TryAddWithoutValidation("User-Agent", USER_AGENT);
                    req.Headers.TryAddWithoutValidation("csrf", CSRF);
                    req.Headers.TryAddWithoutValidation("Cookie", COOKIE);
                    req.Headers.TryAddWithoutValidation("x-amzn-RequestId", Guid.NewGuid().ToString());

                    using var res = await _http.SendAsync(req);
                    if (DEBUG)
                    {
                        var text = await res.Content.ReadAsStringAsync();
                        Console.WriteLine($"Response Status Code: {(int)res.StatusCode}");
                        Console.WriteLine($"Response Text: {text}");
                    }

                    // Verify deletion
                    if (await CheckDeviceDeletedAsync(entityId))
                    {
                        if (DEBUG) Console.WriteLine($"Entity {name}:{entityId} successfully deleted.");
                        deletionSuccess = true;
                        break;
                    }
                    else
                    {
                        Console.WriteLine($"Entity {name}:{entityId} was not deleted. Attempt {attempt + 1}.");
                        break; // matches the Python logic (breaks after first "not deleted" log)
                    }
                }

                if (SHOULD_SLEEP)
                    await Task.Delay(TimeSpan.FromMilliseconds(200));

                if (!deletionSuccess)
                {
                    failed.Add(new FailureInfo
                    {
                        Name = name,
                        EntityId = entityId,
                        DeviceId = deviceIdForUrl,
                        Description = description
                    });
                }
            }

            if (failed.Any())
            {
                Console.WriteLine("\nFailed to delete the following entities:");
                foreach (var f in failed)
                {
                    Console.WriteLine($"Name: '{f.Name}', Entity ID: '{f.EntityId}', Device ID: '{f.DeviceId}', Description: '{f.Description}'");
                }
            }

            return failed;
        }

        // ======= Step 3: Get GraphQL Endpoints =======
        private static async Task GetGraphqlEndpointsAsync()
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, GRAPHQL_URL);
            AddCommonHeaders(req);
            req.Headers.TryAddWithoutValidation("Cookie", COOKIE);
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-CA,en-CA;q=1.0,ar-CA;q=0.9");
            req.Headers.TryAddWithoutValidation("csrf", CSRF);
            req.Headers.TryAddWithoutValidation("x-amzn-RequestId", Guid.NewGuid().ToString());
            req.Headers.TryAddWithoutValidation("User-Agent", USER_AGENT);
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
            req.Headers.TryAddWithoutValidation("x-amzn-alexa-app", X_AMZN_ALEXA_APP);
            req.Headers.TryAddWithoutValidation("Accept", "application/json; charset=utf-8");

            var payload = new GraphQlQuery
            {
                Query = """
                        query CustomerSmartHome {
                          endpoints(endpointsQueryParams: { paginationParams: { disablePagination: true } }) {
                            items {
                              friendlyName
                              legacyAppliance {
                                applianceId
                                mergedApplianceIds
                                connectedVia
                                applianceKey
                                appliancePairs
                                modelName
                                friendlyDescription
                                version
                                friendlyName
                                manufacturerName
                              }
                            }
                          }
                        }
                        """
            };

            req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var body = await res.Content.ReadAsStringAsync();

            await File.WriteAllTextAsync(GRAPHQL_FILE, body, Encoding.UTF8);

            if (DEBUG)
            {
                Console.WriteLine("GraphQL response saved to graphql.json");
            }
        }

        // ======= Step 4: Delete Endpoints =======
        private static async Task<List<FailureInfo>> DeleteEndpointsAsync()
        {
            var failed = new List<FailureInfo>();

            if (!File.Exists(GRAPHQL_FILE))
                return failed;

            var json = await File.ReadAllTextAsync(GRAPHQL_FILE, Encoding.UTF8);
            GraphQlResponse? resp = null;
            try
            {
                resp = JsonSerializer.Deserialize<GraphQlResponse>(json, JsonOpts);
            }
            catch { /* tolerant */ }

            var items = resp?.Data?.Endpoints?.Items ?? Enumerable.Empty<EndpointItem>();

            foreach (var item in items)
            {
                var legacy = item.LegacyAppliance;
                var description = legacy?.FriendlyDescription ?? string.Empty;
                var manufacturer = legacy?.ManufacturerName ?? string.Empty;

                if (!manufacturer.Contains(DESCRIPTION_FILTER_TEXT, StringComparison.OrdinalIgnoreCase))
                    continue;

                var entityId = legacy?.ApplianceKey ?? string.Empty;
                var name = item.FriendlyName ?? string.Empty;
                var deviceIdForUrl = BuildDeviceIdForUrl(description);

                Console.WriteLine($"Name: '{name}', Entity ID: '{entityId}', Device ID: '{deviceIdForUrl}', Description: '{description}'");

                var url = $"{DELETE_URL_PREFIX}{deviceIdForUrl}";
                var deletionSuccess = false;

                for (var attempt = 0; attempt < 4; attempt++)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                    AddCommonHeaders(req);
                    req.Headers.TryAddWithoutValidation("x-amzn-alexa-app", X_AMZN_ALEXA_APP);
                    req.Headers.TryAddWithoutValidation("Accept", "application/json; charset=utf-8");
                    req.Headers.TryAddWithoutValidation("User-Agent", USER_AGENT);
                    req.Headers.TryAddWithoutValidation("Accept-Language", "en-CA,en-CA;q=1.0,ar-CA;q=0.9");
                    req.Headers.TryAddWithoutValidation("csrf", CSRF);
                    req.Headers.TryAddWithoutValidation("Cookie", COOKIE);
                    req.Headers.TryAddWithoutValidation("x-amzn-RequestId", Guid.NewGuid().ToString());

                    using var res = await _http.SendAsync(req);
                    if (DEBUG)
                    {
                        var text = await res.Content.ReadAsStringAsync();
                        Console.WriteLine($"Response Status Code: {(int)res.StatusCode}");
                        Console.WriteLine($"Response Text: {text}");
                    }

                    if (await CheckDeviceDeletedAsync(entityId))
                    {
                        if (DEBUG) Console.WriteLine($"Endpoint {name}:{entityId} successfully deleted.");
                        deletionSuccess = true;
                        break;
                    }
                    else
                    {
                        Console.WriteLine($"Entity {name}:{entityId} was not deleted. Attempt {attempt + 1}.");
                        break; // match Python’s control flow
                    }
                }

                if (SHOULD_SLEEP)
                    await Task.Delay(TimeSpan.FromMilliseconds(200));

                if (!deletionSuccess)
                {
                    failed.Add(new FailureInfo
                    {
                        Name = name,
                        EntityId = entityId,
                        DeviceId = deviceIdForUrl,
                        Description = description
                    });
                }
            }

            if (failed.Any())
            {
                Console.WriteLine("\nFailed to delete the following endpoints:");
                foreach (var f in failed)
                {
                    Console.WriteLine($"Name: '{f.Name}', Entity ID: '{f.EntityId}', Device ID: '{f.DeviceId}', Description: '{f.Description}'");
                }
            }

            return failed;
        }

        // ======= Helpers =======
        private static void AddCommonHeaders(HttpRequestMessage req)
        {
            // NOTE: HttpClient will set Host from the URI. 
            // Some headers like "Connection" and "Content-Length" are restricted and omitted intentionally.
            req.Headers.ConnectionClose = false;
        }

        private static string BuildDeviceIdForUrl(string description) =>
            (description ?? string.Empty)
                .Replace(".", "%23")
                .Replace(" via Home Assistant", "", StringComparison.OrdinalIgnoreCase)
                .ToLowerInvariant();

        private static List<T> DeserializeListSafe<T>(string json)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<T>>(json, JsonOpts);
                if (list != null) return list;
            }
            catch { /* fallback below */ }

            // Fallback if root is not an array or models mismatch
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return JsonSerializer.Deserialize<List<T>>(doc.RootElement.GetRawText(), JsonOpts) ?? new();
                }
            }
            catch { /* give up */ }

            return new();
        }
    }

    // ===== Models to match the fields actually used =====
    internal sealed class Entity
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }

    internal sealed class FailureInfo
    {
        public string Name { get; set; } = "";
        public string EntityId { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string Description { get; set; } = "";
    }

    internal sealed class GraphQlQuery
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = "";
    }

    internal sealed class GraphQlResponse
    {
        [JsonPropertyName("data")] public GraphQlData? Data { get; set; }
    }

    internal sealed class GraphQlData
    {
        [JsonPropertyName("endpoints")] public GraphQlEndpoints? Endpoints { get; set; }
    }

    internal sealed class GraphQlEndpoints
    {
        [JsonPropertyName("items")] public List<EndpointItem>? Items { get; set; }
    }

    internal sealed class EndpointItem
    {
        [JsonPropertyName("friendlyName")] public string? FriendlyName { get; set; }
        [JsonPropertyName("legacyAppliance")] public LegacyAppliance? LegacyAppliance { get; set; }
    }

    internal sealed class LegacyAppliance
    {
        [JsonPropertyName("applianceKey")] public string? ApplianceKey { get; set; }
        [JsonPropertyName("friendlyDescription")] public string? FriendlyDescription { get; set; }
        [JsonPropertyName("manufacturerName")] public string? ManufacturerName { get; set; }
    }

    // ===== JsonDocument helper =====
    internal static class JsonExtensions
    {
        public static string? GetPropertyOrDefault(this JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => v.GetRawText()
            };
        }
    }
}
