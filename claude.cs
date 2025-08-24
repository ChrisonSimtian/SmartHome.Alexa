using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;

namespace AlexaApiManager
{
    /// <summary>
    /// This class is used to interact with the Amazon Alexa API.
    /// 
    /// It contains four main methods: GetEntitiesAsync, DeleteEntitiesAsync, GetGraphqlEndpointsAsync, and DeleteEndpointsAsync.
    /// 
    /// GetEntitiesAsync sends a GET request to retrieve entities related to the Amazon Alexa skill.
    /// The response is saved to a JSON file if it's not empty.
    /// 
    /// DeleteEntitiesAsync sends DELETE requests to remove entities related to the Amazon Alexa skill.
    /// 
    /// GetGraphqlEndpointsAsync sends a POST request to retrieve specific properties of endpoints using a GraphQL query.
    /// The response is saved to a JSON file.
    /// 
    /// DeleteEndpointsAsync sends DELETE requests to remove endpoints related to the Amazon Alexa skill.
    /// 
    /// The class uses predefined headers and parameters for the requests, which are defined as constants.
    /// </summary>
    public class AlexaApiManager
    {
        // Settings
        private const bool DEBUG = false; // set this to true if you want to see more output
        private const bool SHOULD_SLEEP = false; // set this to true if you want to add a delay between each delete request
        private const string DESCRIPTION_FILTER_TEXT = "Home Assistant";

        // CHANGE THESE TO MATCH YOUR SETUP
        private const string HOST = "na-api-alexa.amazon.ca";
        private const string USER_AGENT = "AppleWebKit PitanguiBridge/2.2.635412.0-[HARDWARE=iPhone17_3][SOFTWARE=18.2][DEVICE=iPhone]";
        private const string ROUTINE_VERSION = "3.0.255246";
        private const string COOKIE = ";at-acbca=\"LONG STRING\";sess-at-acbca=\"SHORT STRING\";session-id=000-0000000-0000000;session-id-time=2366612930l;session-token=LOING_STRING;ubid-acbca=000-0000000-00000;x-acbca=\"SHORT_STRING\";csrf=NUMBER";
        private const string X_AMZN_ALEXA_APP = "LONG_STRING";
        private const string CSRF = "NUMBER"; // should look something like this: 'somenumber'; should match the cookie 
        private const string DELETE_SKILL = "SKILL_LONG_STRING";

        // Constants
        private const string DATA_FILE = "data.json";
        private const string GRAPHQL_FILE = "graphql.json";
        private static readonly string GET_URL = $"https://{HOST}/api/behaviors/entities?skillId=amzn1.ask.1p.smarthome";
        private static readonly string DELETE_URL = $"https://{HOST}/api/phoenix/appliance/{DELETE_SKILL}%3D%3D_";
        private const string ACCEPT_HEADER = "application/json; charset=utf-8";

        private static readonly HttpClient httpClient = new HttpClient();

        public class Entity
        {
            public string id { get; set; }
            public string displayName { get; set; }
            public string description { get; set; }
        }

        public class LegacyAppliance
        {
            public string applianceId { get; set; }
            public List<string> mergedApplianceIds { get; set; }
            public string connectedVia { get; set; }
            public string applianceKey { get; set; }
            public List<string> appliancePairs { get; set; }
            public string modelName { get; set; }
            public string friendlyDescription { get; set; }
            public string version { get; set; }
            public string friendlyName { get; set; }
            public string manufacturerName { get; set; }
        }

        public class EndpointItem
        {
            public string friendlyName { get; set; }
            public LegacyAppliance legacyAppliance { get; set; }
        }

        public class EndpointsData
        {
            public List<EndpointItem> items { get; set; }
        }

        public class GraphqlData
        {
            public EndpointsData endpoints { get; set; }
        }

        public class GraphqlResponse
        {
            public GraphqlData data { get; set; }
        }

        public class FailedDeletion
        {
            public string Name { get; set; }
            public string EntityId { get; set; }
            public string DeviceId { get; set; }
            public string Description { get; set; }
        }

        /// <summary>
        /// Sends a GET request to the specified URL to retrieve entities related to the Amazon Alexa skill.
        /// The method saves the response to a JSON file if it's not empty.
        /// </summary>
        /// <param name="url">The URL to send the GET request to. Defaults to the predefined GET_URL.</param>
        /// <returns>The JSON response as a list of Entity objects.</returns>
        public static async Task<List<Entity>> GetEntitiesAsync(string url = null)
        {
            if (string.IsNullOrEmpty(url))
                url = GET_URL;

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Host", HOST);
            request.Headers.Add("Routines-Version", ROUTINE_VERSION);
            request.Headers.Add("Cookie", COOKIE);
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Add("x-amzn-alexa-app", X_AMZN_ALEXA_APP);
            request.Headers.Add("Accept", ACCEPT_HEADER);
            request.Headers.Add("User-Agent", USER_AGENT);

            var response = await httpClient.SendAsync(request);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!string.IsNullOrWhiteSpace(responseText))
            {
                var entities = JsonSerializer.Deserialize<List<Entity>>(responseText);
                await File.WriteAllTextAsync(DATA_FILE, responseText, Encoding.UTF8);
                return entities;
            }
            else
            {
                Console.WriteLine("Empty response received from server.");
                return new List<Entity>();
            }
        }

        /// <summary>
        /// Sends a GET request to check if the device was deleted.
        /// </summary>
        /// <param name="entityId">The ID of the entity to check.</param>
        /// <returns>True if the device was deleted, False otherwise.</returns>
        public static async Task<bool> CheckDeviceDeletedAsync(string entityId)
        {
            var url = $"https://{HOST}/api/smarthome/v1/presentation/devices/control/{entityId}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-amzn-RequestId", Guid.NewGuid().ToString());
            request.Headers.Add("Host", HOST);
            request.Headers.Add("User-Agent", USER_AGENT);
            request.Headers.Add("Cookie", COOKIE);
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Add("Accept", ACCEPT_HEADER);
            request.Headers.Add("x-amzn-alexa-app", X_AMZN_ALEXA_APP);

            try
            {
                var response = await httpClient.SendAsync(request);
                if (DEBUG)
                {
                    Console.WriteLine($"Check device deleted response status code: {response.StatusCode}");
                    var responseText = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Check device deleted response text: {responseText}");
                }
                return response.StatusCode == System.Net.HttpStatusCode.NotFound;
            }
            catch (Exception ex)
            {
                if (DEBUG)
                    Console.WriteLine($"Error checking device deletion: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends DELETE requests to remove entities related to the Amazon Alexa skill.
        /// Reads entity data from a JSON file, and for each entity, constructs a URL and sends a DELETE request.
        /// </summary>
        /// <returns>A list of FailedDeletion objects containing information about failed deletions.</returns>
        public static async Task<List<FailedDeletion>> DeleteEntitiesAsync()
        {
            var failedDeletions = new List<FailedDeletion>();

            if (!File.Exists(DATA_FILE))
            {
                Console.WriteLine($"Data file {DATA_FILE} not found. Run GetEntitiesAsync first.");
                return failedDeletions;
            }

            var jsonContent = await File.ReadAllTextAsync(DATA_FILE, Encoding.UTF8);
            var entities = JsonSerializer.Deserialize<List<Entity>>(jsonContent);

            foreach (var item in entities)
            {
                var description = item.description ?? "";
                if (description.Contains(DESCRIPTION_FILTER_TEXT))
                {
                    var entityId = item.id;
                    var name = item.displayName;
                    var deviceIdForUrl = description.Replace(".", "%23").Replace(" via Home Assistant", "").ToLower();
                    
                    Console.WriteLine($"Name: '{name}', Entity ID: '{entityId}', Device ID: '{deviceIdForUrl}', Description: '{description}'");
                    
                    var url = $"{DELETE_URL}{deviceIdForUrl}";
                    var deletionSuccess = false;

                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        var request = new HttpRequestMessage(HttpMethod.Delete, url);
                        request.Headers.Add("Host", HOST);
                        request.Headers.Add("Content-Length", "0");
                        request.Headers.Add("x-amzn-alexa-app", X_AMZN_ALEXA_APP);
                        request.Headers.Add("Connection", "keep-alive");
                        request.Headers.Add("Accept", ACCEPT_HEADER);
                        request.Headers.Add("User-Agent", USER_AGENT);
                        request.Headers.Add("csrf", CSRF);
                        request.Headers.Add("Cookie", COOKIE);
                        request.Headers.Add("x-amzn-RequestId", Guid.NewGuid().ToString());

                        var response = await httpClient.SendAsync(request);

                        if (DEBUG)
                        {
                            Console.WriteLine($"Response Status Code: {response.StatusCode}");
                            var responseText = await response.Content.ReadAsStringAsync();
                            Console.WriteLine($"Response Text: {responseText}");
                        }

                        if (await CheckDeviceDeletedAsync(entityId))
                        {
                            if (DEBUG)
                                Console.WriteLine($"Entity {name}:{entityId} successfully deleted.");
                            deletionSuccess = true;
                            break;
                        }
                        else
                        {
                            Console.WriteLine($"Entity {name}:{entityId} was not deleted. Attempt {attempt + 1}.");
                            break;
                        }

                        if (SHOULD_SLEEP)
                            await Task.Delay(200);
                    }

                    if (!deletionSuccess)
                    {
                        failedDeletions.Add(new FailedDeletion
                        {
                            Name = name,
                            EntityId = entityId,
                            DeviceId = deviceIdForUrl,
                            Description = description
                        });
                    }
                }
            }

            if (failedDeletions.Count > 0)
            {
                Console.WriteLine("\nFailed to delete the following entities:");
                foreach (var failure in failedDeletions)
                {
                    Console.WriteLine($"Name: '{failure.Name}', Entity ID: '{failure.EntityId}', Device ID: '{failure.DeviceId}', Description: '{failure.Description}'");
                }
            }

            return failedDeletions;
        }

        /// <summary>
        /// Sends a POST request to retrieve specific properties of endpoints using a GraphQL query.
        /// The method saves the response to a JSON file.
        /// </summary>
        /// <returns>The JSON response as a GraphqlResponse object.</returns>
        public static async Task<GraphqlResponse> GetGraphqlEndpointsAsync()
        {
            var url = $"https://{HOST}/nexus/v1/graphql";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            
            request.Headers.Add("Cookie", COOKIE);
            request.Headers.Add("Host", HOST);
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Add("Accept-Language", "en-CA,en-CA;q=1.0,ar-CA;q=0.9");
            request.Headers.Add("csrf", CSRF);
            request.Headers.Add("x-amzn-RequestId", Guid.NewGuid().ToString());
            request.Headers.Add("User-Agent", USER_AGENT);
            request.Headers.Add("Accept-Encoding", "gzip, deflate, br");
            request.Headers.Add("x-amzn-alexa-app", X_AMZN_ALEXA_APP);
            request.Headers.Add("Accept", ACCEPT_HEADER);

            var queryData = new
            {
                query = @"
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
        }"
            };

            var jsonContent = JsonSerializer.Serialize(queryData);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            request.Headers.Add("Content-Length", jsonContent.Length.ToString());

            var response = await httpClient.SendAsync(request);
            var responseText = await response.Content.ReadAsStringAsync();
            var responseJson = JsonSerializer.Deserialize<GraphqlResponse>(responseText);

            await File.WriteAllTextAsync(GRAPHQL_FILE, responseText, Encoding.UTF8);

            return responseJson;
        }

        /// <summary>
        /// Sends DELETE requests to remove endpoints related to the Amazon Alexa skill.
        /// Reads endpoint data from a JSON file, and for each endpoint, constructs a URL and sends a DELETE request.
        /// </summary>
        /// <returns>A list of FailedDeletion objects containing information about failed deletions.</returns>
        public static async Task<List<FailedDeletion>> DeleteEndpointsAsync()
        {
            var failedDeletions = new List<FailedDeletion>();

            if (!File.Exists(GRAPHQL_FILE))
            {
                Console.WriteLine($"GraphQL file {GRAPHQL_FILE} not found. Run GetGraphqlEndpointsAsync first.");
                return failedDeletions;
            }

            var jsonContent = await File.ReadAllTextAsync(GRAPHQL_FILE, Encoding.UTF8);
            var responseJson = JsonSerializer.Deserialize<GraphqlResponse>(jsonContent);

            foreach (var item in responseJson.data.endpoints.items)
            {
                var description = item.legacyAppliance?.friendlyDescription ?? "";
                var manufacturerName = item.legacyAppliance?.manufacturerName ?? "";
                
                if (manufacturerName.Contains(DESCRIPTION_FILTER_TEXT))
                {
                    var entityId = item.legacyAppliance.applianceKey;
                    var name = item.friendlyName;
                    var deviceIdForUrl = description.Replace(".", "%23").Replace(" via Home Assistant", "").ToLower();
                    
                    Console.WriteLine($"Name: '{name}', Entity ID: '{entityId}', Device ID: '{deviceIdForUrl}', Description: '{description}'");
                    
                    var url = $"{DELETE_URL}{deviceIdForUrl}";
                    var deletionSuccess = false;

                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        var request = new HttpRequestMessage(HttpMethod.Delete, url);
                        request.Headers.Add("Host", HOST);
                        request.Headers.Add("Content-Length", "0");
                        request.Headers.Add("x-amzn-alexa-app", X_AMZN_ALEXA_APP);
                        request.Headers.Add("Connection", "keep-alive");
                        request.Headers.Add("Accept", ACCEPT_HEADER);
                        request.Headers.Add("User-Agent", USER_AGENT);
                        request.Headers.Add("Accept-Language", "en-CA,en-CA;q=1.0,ar-CA;q=0.9");
                        request.Headers.Add("csrf", CSRF);
                        request.Headers.Add("Cookie", COOKIE);
                        request.Headers.Add("x-amzn-RequestId", Guid.NewGuid().ToString());

                        var response = await httpClient.SendAsync(request);

                        if (DEBUG)
                        {
                            Console.WriteLine($"Response Status Code: {response.StatusCode}");
                            var responseText = await response.Content.ReadAsStringAsync();
                            Console.WriteLine($"Response Text: {responseText}");
                        }

                        if (await CheckDeviceDeletedAsync(entityId))
                        {
                            if (DEBUG)
                                Console.WriteLine($"Entity {name}:{entityId} successfully deleted.");
                            deletionSuccess = true;
                            break;
                        }
                        else
                        {
                            Console.WriteLine($"Entity {name}:{entityId} was not deleted. Attempt {attempt + 1}.");
                            break;
                        }

                        if (SHOULD_SLEEP)
                            await Task.Delay(200);
                    }

                    if (!deletionSuccess)
                    {
                        failedDeletions.Add(new FailedDeletion
                        {
                            Name = name,
                            EntityId = entityId,
                            DeviceId = deviceIdForUrl,
                            Description = description
                        });
                    }
                }
            }

            if (failedDeletions.Count > 0)
            {
                Console.WriteLine("\nFailed to delete the following endpoints:");
                foreach (var failure in failedDeletions)
                {
                    Console.WriteLine($"Name: '{failure.Name}', Entity ID: '{failure.EntityId}', Device ID: '{failure.DeviceId}', Description: '{failure.Description}'");
                }
            }

            return failedDeletions;
        }

        public static async Task Main(string[] args)
        {
            try
            {
                await GetEntitiesAsync();
                var failedEntities = await DeleteEntitiesAsync();
                
                await GetGraphqlEndpointsAsync();
                var failedEndpoints = await DeleteEndpointsAsync();

                if (failedEntities.Count > 0 || failedEndpoints.Count > 0)
                {
                    Console.WriteLine("\nSummary of all failed deletions:");
                    if (failedEntities.Count > 0)
                    {
                        Console.WriteLine("\nFailed Entities:");
                        foreach (var failure in failedEntities)
                        {
                            Console.WriteLine($"Name: '{failure.Name}', Entity ID: '{failure.EntityId}'");
                        }
                    }
                    if (failedEndpoints.Count > 0)
                    {
                        Console.WriteLine("\nFailed Endpoints:");
                        foreach (var failure in failedEndpoints)
                        {
                            Console.WriteLine($"Name: '{failure.Name}', Entity ID: '{failure.EntityId}'");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"Done, removed all entities and endpoints with a manufacturer name matching: {DESCRIPTION_FILTER_TEXT}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
                if (DEBUG)
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
            finally
            {
                httpClient.Dispose();
            }
        }
    }
}
