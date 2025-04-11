using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Newtonsoft.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using static Grpc.Core.Metadata;
using Microsoft.PowerPlatform.Dataverse.Client.Extensions;
using static Microsoft.ApplicationInsights.MetricDimensionNames.TelemetryContext;
using static SimulationAttack_Dataverse.Function1.UserCoverage;
using static SimulationAttack_Dataverse.Function1;
using System.Net;
using Azure.Core;
using Microsoft.Rest;


namespace SimulationAttack_Dataverse
{
    public class Function1
    {
        private readonly ILogger _logger;
        private static string _accessToken = string.Empty;
        private static DateTime _tokenExpiryTime = DateTime.UtcNow;
        private static readonly HttpClient client = new HttpClient();
        private static readonly HttpClient _httpClient = new HttpClient();

        public Function1(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<Function1>();
        }

        [Function("MDO-SimulationData-Dataverse")]
        //public async Task Run([TimerTrigger("0 0 6 * * *")] TimerInfo myTimer) // Once a day at 2am
        public async Task Run([TimerTrigger("0 0 */2 * * *")] TimerInfo myTimer) //Every 2hrs
        {
            _logger.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            if (myTimer.ScheduleStatus is not null)
            {
                _logger.LogInformation($"Next timer schedule at: {myTimer.ScheduleStatus.Next}");
            }

            try
            {
                string tenantId = Environment.GetEnvironmentVariable("TenantId");
                string clientId = Environment.GetEnvironmentVariable("ClientId");
                string clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
                string environmentName = Environment.GetEnvironmentVariable("EnvironmentName") ?? "Global";
                string DataverseConnection = Environment.GetEnvironmentVariable("DataverseConnection");
                string SimulationTable = Environment.GetEnvironmentVariable("SimulationTable");
                string CoverageUsersTable = Environment.GetEnvironmentVariable("CoverageUsersTable"); // Table for user coverage
                string SimulationUsersTable = Environment.GetEnvironmentVariable("SimulationUsersTable"); // Table for SimulationUsersTable
                string TrainingUserTable = Environment.GetEnvironmentVariable("TrainingUserTable"); // Table for SimulationUsersTable

                // Step 1: Get Microsoft Graph Endpoints
                var targetCloud = Environment.GetEnvironmentVariable("EnvironmentName");
                string graphBaseUrl;

                switch (targetCloud)
                {
                    case "AzureUSDoD":
                        graphBaseUrl = "https://dod-graph.microsoft.us/";
                        break;
                    case "AzureGov":
                        graphBaseUrl = "https://graph.microsoft.us/";
                        break;
                    case "GCC":
                        graphBaseUrl = "https://graph.microsoft.us/";
                        break;
                    default:
                        graphBaseUrl = "https://graph.microsoft.com/";
                        break;
                }

                // Step 2: Get access token for Microsoft Graph
                var accessToken = await GetAccessToken(graphBaseUrl, _logger);

                // Making Connection to Dataverse
                var dataverseConnection = Environment.GetEnvironmentVariable("DataverseConnection");
                if (string.IsNullOrEmpty(dataverseConnection))
                {
                    throw new ArgumentException("Dataverse connection string is not set or is invalid.");
                }
                var serviceClient = new ServiceClient(dataverseConnection);

                // Step 3: Fetch simulations
                var simulations = await GetSimulations(serviceClient, SimulationTable, graphBaseUrl, accessToken, _logger);

                // Start writing simulations concurrently
                var simulationTasks = simulations
                    .Select(simulation => WriteSimulationToDataverse(serviceClient, simulation, SimulationTable, _logger))
                    .ToList();

                await Task.WhenAll(simulationTasks); // Wait for all simulation writes to finish

                // Step 4: Get all simulation IDs
                var simulationIds = simulations.Select(sim => sim.Id).ToList();

                // Step 5: Retrieve sync statuses from Dataverse
                var syncStatuses = await RetrieveSyncStatusesForSimulations(serviceClient, SimulationTable, simulationIds, _logger);

                // Step 6: Filter simulations with syncStatus != "Completed"
                var incompleteSimulations = simulations
                    .Where(sim =>
                        !syncStatuses.TryGetValue(sim.Id, out var status) ||
                        !status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                    )
                    .ToList();

                // Filter by date range first
                DateTime startDate = DateTime.UtcNow.AddDays(-160).Date;
                DateTime now = DateTime.UtcNow;

                var filteredByDate = incompleteSimulations
                    .Where(sim =>
                        sim.CompletionDateTime.HasValue &&
                        sim.CompletionDateTime.Value >= startDate &&
                        sim.CompletionDateTime.Value <= now)
                    .Take(2) // Now we take top 5 *after* date filtering
                    .ToList();


                if (filteredByDate?.Any() == true)
                {
                    foreach (var simulation in filteredByDate)
                    {
                        try
                        {
                            var simulationUsersList = await GetSimulationUsers(graphBaseUrl, accessToken, simulation.Id, _logger);

                            foreach (var user in simulationUsersList)
                            {
                                try
                                {
                                    await WriteSimulationUsersToDataverse(serviceClient, user, SimulationUsersTable, simulation.Id, _logger);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, $"Failed to write user for simulation {simulation.Id}");
                                }
                            }

                            await MarkSimulationAsProcessed(serviceClient, simulation.Id, SimulationTable, _logger);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed processing simulation {simulation.Id}");
                        }
                    }
                }

                // Step 5: Fetch TrainingUserCoverage data
                var TrainingUserCoverage = await GetTrainingUserCoverage(graphBaseUrl, accessToken, _logger);

                // Step 6: Write TrainingUserCoverage to Dataverse
                foreach (var TrainingUsers in TrainingUserCoverage)
                {
                    await WriteTrainingUserCoverageToDataverse(serviceClient, TrainingUsers, TrainingUserTable, _logger);
                }

                // Step 8: Fetch user coverage data - Long Process 
                //var userCoverage = await GetAllAttackSimulationUserCoverage(graphBaseUrl, _logger);

                // Step 9: Write coverage data to Dataverse - Long Process
                //var writeTasks = userCoverage.Select(user => WriteUserCoverageToDataverse(serviceClient, user, CoverageUsersTable, _logger));
                //await Task.WhenAll(writeTasks);

            }
            catch (Exception ex)
            {
                _logger.LogError($"An error occurred: {ex.Message}");
            }
        }


        private static async Task<string> GetAccessToken(string uri, ILogger logger)
        {
            // If token is still valid, return it
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiryTime)
            {
                return _accessToken;
            }

            // Fetch new token
            var clientSecret = Environment.GetEnvironmentVariable("ClientSecret", EnvironmentVariableTarget.Process);
            var clientId = Environment.GetEnvironmentVariable("ClientId", EnvironmentVariableTarget.Process);
            var TenantId = Environment.GetEnvironmentVariable("TenantId", EnvironmentVariableTarget.Process);
            var TargetCloud = Environment.GetEnvironmentVariable("EnvironmentName", EnvironmentVariableTarget.Process);

            if (string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(TenantId) || string.IsNullOrEmpty(TargetCloud))
            {
                var missingVars = new List<string>();
                if (string.IsNullOrEmpty(clientSecret)) missingVars.Add("ClientSecret");
                if (string.IsNullOrEmpty(clientId)) missingVars.Add("ClientId");
                if (string.IsNullOrEmpty(TenantId)) missingVars.Add("TenantId");
                if (string.IsNullOrEmpty(TargetCloud)) missingVars.Add("TargetCloud");

                var errorMessage = $"Missing required environment variables: {string.Join(", ", missingVars)}";
                logger.LogError(errorMessage);
                throw new ArgumentNullException(errorMessage);
            }

            var tokenUri = TargetCloud == "AzureUSDoD" || TargetCloud == "AzureGov"
                ? $"https://login.microsoftonline.us/{TenantId}/oauth2/token"
                : $"https://login.microsoftonline.com/{TenantId}/oauth2/token";

            var tokenRequestContent = new[]
            {
        new KeyValuePair<string, string>("grant_type", "client_credentials"),
        new KeyValuePair<string, string>("client_id", clientId),
        new KeyValuePair<string, string>("client_secret", clientSecret),
        new KeyValuePair<string, string>("resource", uri)
    };

            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUri)
            {
                Content = new FormUrlEncodedContent(tokenRequestContent)
            };

            logger.LogInformation($"Requesting new access token from: {tokenUri}");

            try
            {
                var tokenResponse = await _httpClient.SendAsync(tokenRequest);
                tokenResponse.EnsureSuccessStatusCode();
                var tokenContent = await tokenResponse.Content.ReadAsStringAsync();
                var tokenData = JsonConvert.DeserializeObject<dynamic>(tokenContent);

                _accessToken = tokenData.access_token;
                int expiresIn = tokenData.expires_in; // Token expiration in seconds
                _tokenExpiryTime = DateTime.UtcNow.AddSeconds(expiresIn - 60); // Refresh 1 min before expiry

                logger.LogInformation($"New access token acquired, expires in {expiresIn} seconds.");

                return _accessToken;
            }
            catch (Exception ex)
            {
                var errorMessage = $"Error getting access token from {tokenUri}: {ex.Message}";
                logger.LogError(errorMessage);
                throw new InvalidOperationException(errorMessage);
            }
        }
        private static async Task<List<Simulation>> GetSimulations(ServiceClient serviceClient, string SimulationTable, string graphBaseUrl, string accessToken, ILogger _logger)
        {
            var simulations = new List<Simulation>();
            string requestUrl = $"{graphBaseUrl}/v1.0/security/attackSimulation/simulations";

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            do
            {
                try
                {
                    var response = await client.GetAsync(requestUrl);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError($"Error: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
                        break;
                    }

                    var responseBody = await response.Content.ReadAsStringAsync();
                    var simulationResponse = JsonConvert.DeserializeObject<SimulationResponse>(responseBody);

                    if (simulationResponse?.Value != null)
                    {
                        simulations.AddRange(simulationResponse.Value);
                    }
                    else
                    {
                        _logger.LogWarning("No simulations found in the response.");
                    }

                    // Check for pagination link
                    requestUrl = simulationResponse?.NextLink;

                    if (!string.IsNullOrEmpty(requestUrl))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1)); // Ensure delay happens before next request
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Exception: {ex.Message}");
                    break;
                }

            } while (!string.IsNullOrEmpty(requestUrl));

            return simulations;
        }
        private static async Task<List<UserCoverage>> GetAllAttackSimulationUserCoverage(string graphBaseUrl, ILogger _logger)
        {
            _logger.LogInformation("Starting GetAllAttackSimulationUserCoverage...");

            var client = new HttpClient(new SocketsHttpHandler { EnableMultipleHttp2Connections = true });

            var allUserCoverage = new List<UserCoverage>();
            string nextLink = $"{graphBaseUrl}/v1.0/reports/security/getAttackSimulationSimulationUserCoverage";
            DateTime lastLogTime = DateTime.UtcNow;

            string accessToken = await GetAccessToken(graphBaseUrl, _logger);
            DateTime tokenExpiryTime = DateTime.UtcNow.AddMinutes(50); // Assuming a 50-minute expiry for the token

            int retryCount = 0;
            const int maxRetries = 5;

            do
            {
                try
                {
                    // Refresh token only when it's about to expire or expired
                    if (DateTime.UtcNow >= tokenExpiryTime)
                    {
                        _logger.LogInformation("Refreshing access token...");
                        accessToken = await GetAccessToken(graphBaseUrl, _logger);
                        tokenExpiryTime = DateTime.UtcNow.AddMinutes(50); // Reset expiry time
                    }
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                    var response = await client.GetAsync(nextLink);
                    //_logger.LogInformation($"Fetching data from: {nextLink}");
                    //_logger.LogInformation($"Response Status: {response.StatusCode}");

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        _logger.LogWarning("Access token expired, refreshing and retrying...");
                        accessToken = await GetAccessToken(graphBaseUrl, _logger);
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                        response = await client.GetAsync(nextLink);
                        _logger.LogInformation($"Retry Response Status: {response.StatusCode}");
                    }

                    if (response.StatusCode == HttpStatusCode.TooManyRequests) // 429 Rate Limit Handling
                    {
                        if (response.Headers.TryGetValues("Retry-After", out var values) && int.TryParse(values.FirstOrDefault(), out int retryAfter))
                        {
                            _logger.LogWarning($"Rate limit hit. Waiting {retryAfter} seconds before retrying...");
                            await Task.Delay(TimeSpan.FromSeconds(retryAfter));
                        }
                        else
                        {
                            _logger.LogWarning("Rate limit hit. Waiting 30 seconds before retrying...");
                            await Task.Delay(TimeSpan.FromSeconds(30)); // Default wait if Retry-After is not provided
                        }
                        continue; // Retry instead of moving forward
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        string errorContent = await response.Content.ReadAsStringAsync();
                        _logger.LogError($"Error: {response.StatusCode} - {errorContent}");

                        if (retryCount < maxRetries)
                        {
                            _logger.LogWarning($"Retrying request... Attempt {retryCount + 1}/{maxRetries}");
                            retryCount++;
                            await Task.Delay(TimeSpan.FromSeconds(5 * retryCount)); // Exponential backoff
                            continue;
                        }
                        else
                        {
                            _logger.LogError("Max retries reached. Skipping this batch.");
                            break; // Exit loop after max retries
                        }
                    }

                    var responseBody = await response.Content.ReadAsStringAsync();
                    var userCoverageResponse = JsonConvert.DeserializeObject<UserCoverageResponse>(responseBody);

                    if (userCoverageResponse?.Value != null && userCoverageResponse.Value.Count > 0)
                    {
                        //_logger.LogInformation($"Records retrieved in this batch: {userCoverageResponse.Value.Count}");
                        allUserCoverage.AddRange(userCoverageResponse.Value);
                        retryCount = 0; // Reset retry count on success

                        if ((DateTime.UtcNow - lastLogTime).TotalSeconds >= 60)
                        {
                            _logger.LogInformation($"Total records retrieved so far: {allUserCoverage.Count}");
                            lastLogTime = DateTime.UtcNow;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Received empty data batch. Checking if there are more pages...");
                    }

                    nextLink = userCoverageResponse?.NextLink;

                    if (!string.IsNullOrEmpty(nextLink))
                    {
                        if (response.Headers.TryGetValues("Retry-After", out var retryValues) && int.TryParse(retryValues.FirstOrDefault(), out int retryAfter))
                        {
                            _logger.LogWarning($"Rate limit detected. Waiting {retryAfter} seconds...");
                            await Task.Delay(TimeSpan.FromSeconds(retryAfter));
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(500)); // Reduced delay between requests
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Exception: {ex.Message}\nStackTrace: {ex.StackTrace}");
                    if (retryCount < maxRetries)
                    {
                        _logger.LogWarning($"Retrying due to exception... Attempt {retryCount + 1}/{maxRetries}");
                        retryCount++;
                        await Task.Delay(TimeSpan.FromSeconds(5 * retryCount)); // Exponential backoff
                        continue;
                    }
                    else
                    {
                        _logger.LogError("Max retries reached due to exception. Exiting...");
                        throw;  // Let it bubble up
                    }
                }

            } while (!string.IsNullOrEmpty(nextLink));

            _logger.LogInformation($"Final total records retrieved: {allUserCoverage.Count}");
            return allUserCoverage;
        }
        private static async Task<List<TrainingUserCoverage>> GetTrainingUserCoverage(string graphBaseUrl, string accessToken, ILogger _logger)
        {
            var client = new HttpClient();
            var allTrainingUserCoverage = new List<TrainingUserCoverage>();
            string nextLink = $"{graphBaseUrl}/v1.0/reports/security/getAttackSimulationTrainingUserCoverage";

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            do
            {
                try
                {
                    _logger.LogInformation($"Fetching training user coverage from: {nextLink}");

                    var response = await client.GetAsync(nextLink);

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        _logger.LogWarning("Access token expired. Attempting to refresh token...");
                        accessToken = await GetAccessToken(graphBaseUrl, _logger); // token refresh logic
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                        continue; // Retry with new token
                    }

                    if (response.StatusCode == (HttpStatusCode)429) // Too Many Requests
                    {
                        var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                        _logger.LogWarning($"Rate limit hit. Retrying after {retryAfter.TotalSeconds} seconds...");
                        await Task.Delay(retryAfter);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError($"Error: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
                        break;
                    }

                    var responseBody = await response.Content.ReadAsStringAsync();
                    var trainingUserCoverageResponse = JsonConvert.DeserializeObject<TrainingUserCoverageResponse>(responseBody);

                    if (trainingUserCoverageResponse?.Value != null)
                    {
                        _logger.LogInformation($"Retrieved {trainingUserCoverageResponse.Value.Count} training user coverage records.");
                        allTrainingUserCoverage.AddRange(trainingUserCoverageResponse.Value);
                    }
                    else
                    {
                        _logger.LogWarning("No training user coverage data found in the response.");
                    }

                    // Check for pagination
                    nextLink = trainingUserCoverageResponse?.NextLink;

                    if (!string.IsNullOrEmpty(nextLink))
                    {
                        _logger.LogInformation($"Next page found: {nextLink}. Waiting 1 second before next request...");
                        await Task.Delay(TimeSpan.FromSeconds(1)); // Reduce delay from 5s to 1s for better efficiency
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Exception: {ex.Message}");
                    break;
                }

            } while (!string.IsNullOrEmpty(nextLink));

            _logger.LogInformation($"Total training user coverage records retrieved: {allTrainingUserCoverage.Count}");
            return allTrainingUserCoverage;
        }

        private static async Task<List<SimulationUsers>> GetSimulationUsers(string graphBaseUrl, string accessToken, string id, ILogger _logger)
        {
            var client = new HttpClient(new SocketsHttpHandler { EnableMultipleHttp2Connections = true });

            var allSimulationUsers = new List<SimulationUsers>();
            string nextLink = $"{graphBaseUrl}/v1.0/security/attackSimulation/simulations/{id}/report/simulationUsers";

            do
            {
                try
                {
                    //_logger.LogInformation($"Fetching simulation users from: {nextLink}");

                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    var response = await client.GetAsync(nextLink);

                    //_logger.LogInformation($"Response Status: {response.StatusCode}");

                    // Handle expired token case
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        _logger.LogWarning("Access token expired, refreshing and retrying...");
                        accessToken = await GetAccessToken(graphBaseUrl, _logger); // Refresh token
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                        response = await client.GetAsync(nextLink);
                        _logger.LogInformation($"Retry Response Status: {response.StatusCode}");
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        string errorContent = await response.Content.ReadAsStringAsync();
                        _logger.LogError($"Error: {response.StatusCode} - {errorContent}");
                        break;
                    }

                    var responseBody = await response.Content.ReadAsStringAsync();
                    var simulationUsersResponse = JsonConvert.DeserializeObject<SimulationUsersResponse>(responseBody);

                    if (simulationUsersResponse?.Value != null && simulationUsersResponse.Value.Count > 0)
                    {
                        //_logger.LogInformation($"Retrieved {simulationUsersResponse.Value.Count} simulation user records.");
                        allSimulationUsers.AddRange(simulationUsersResponse.Value);
                    }
                    else
                    {
                        _logger.LogWarning("No simulation users found in the response.");
                    }

                    // Check for the next page
                    nextLink = simulationUsersResponse?.NextLink;

                    if (!string.IsNullOrEmpty(nextLink))
                    {
                        if (response.Headers.TryGetValues("Retry-After", out var values) && int.TryParse(values.FirstOrDefault(), out int retryAfter))
                        {
                            _logger.LogWarning($"Rate limit hit. Waiting {retryAfter} seconds...");
                            await Task.Delay(TimeSpan.FromSeconds(retryAfter));
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(500)); // Reduced delay
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Exception: {ex.Message}\nStackTrace: {ex.StackTrace}");
                    break;
                }

            } while (!string.IsNullOrEmpty(nextLink));

            _logger.LogInformation($"Total simulation user records retrieved: {allSimulationUsers.Count}");
            return allSimulationUsers;
        }

        private static async Task<Entity> RetrieveExistingRecord(ServiceClient serviceClient, string tableName, string fieldName, string fieldValue)
        {
            var query = new QueryExpression(tableName)
            {
                ColumnSet = new ColumnSet(true),
                Criteria = new FilterExpression
                {
                    Conditions =
            {
                new ConditionExpression(fieldName, ConditionOperator.Equal, fieldValue)
            }
                }
            };

            var results = await serviceClient.RetrieveMultipleAsync(query);
            return results.Entities.FirstOrDefault(); // Return the first record found, if any
        }

        private static async Task<Entity> RetrieveExistingRecordSimUser(ServiceClient serviceClient, string tableName, string simulationUserFieldName, string simulationUserFieldValue, string simulationIdFieldName, string simulationIdFieldValue)
        {
            var query = new QueryExpression(tableName)
            {
                ColumnSet = new ColumnSet(true),
                Criteria = new FilterExpression
                {
                    Conditions =
            {
                new ConditionExpression(simulationUserFieldName, ConditionOperator.Equal, simulationUserFieldValue),
                new ConditionExpression(simulationIdFieldName, ConditionOperator.Equal, simulationIdFieldValue)
            }
                }
            };

            var results = await serviceClient.RetrieveMultipleAsync(query);
            return results.Entities.FirstOrDefault(); // Return the first record found, if any
        }

        private static async Task WriteUserCoverageToDataverse(ServiceClient serviceClient, UserCoverage user, string CoverageUsersTable, ILogger logger)
        {
            var tableprefix = Environment.GetEnvironmentVariable("tableprefix", EnvironmentVariableTarget.Process);
            string userJson = JsonConvert.SerializeObject(user.AttackSimulationUser);

            var existingRecord = await RetrieveExistingRecord(serviceClient, CoverageUsersTable, $"{tableprefix}_attacksimulationuser", userJson);

            int processedCount = 0;
            DateTime lastLogTime = DateTime.UtcNow;

            var entity = new Entity(CoverageUsersTable)
            {
                [($"{tableprefix}_simulationcount")] = user.SimulationCount,
                [($"{tableprefix}_latestsimulationdatetime")] = user.LatestSimulationDateTime ?? (DateTime?)null,
                [($"{tableprefix}_clickcount")] = user.ClickCount,
                [($"{tableprefix}_compromisedcount")] = user.CompromisedCount,
                [($"{tableprefix}_attacksimulationuser")] = userJson,
            };

            if (existingRecord != null)
            {
                entity.Id = existingRecord.Id;
                await serviceClient.UpdateAsync(entity);
                //logger.LogInformation($"Updated user coverage record for {user.AttackSimulationUser.UserId}");
            }
            else
            {
                await serviceClient.CreateAsync(entity);
                //logger.LogInformation($"Created new user coverage record for {user.AttackSimulationUser.UserId}");
            }

            // Track the count of processed records
            Interlocked.Increment(ref processedCount);

            // Log every 60 seconds
            if ((DateTime.UtcNow - lastLogTime).TotalSeconds >= 60)
            {
                logger.LogInformation($"Total records processed so far: {processedCount}");
                lastLogTime = DateTime.UtcNow;
            }
        }
        private static async Task WriteSimulationToDataverse(ServiceClient serviceClient, Simulation simulation, string SimulationTable, ILogger _logger)
        {
            var tableprefix = Environment.GetEnvironmentVariable("tableprefix", EnvironmentVariableTarget.Process);
            // Log the table name for verification
            _logger.LogInformation($"Writing to Dataverse table: {SimulationTable}");

            try
            {
                // Convert simulation.Id from string to Guid
                if (Guid.TryParse(simulation.Id, out Guid simulationId))
                {
                    // Attempt to retrieve the existing record
                    var existingRecord = await RetrieveExistingRecord(serviceClient, SimulationTable, ($"{tableprefix}_id"), simulation.Id);

                    // Create the entity to be created or updated
                    var entity = new Entity(SimulationTable);

                    // Set the common fields
                    entity[($"{tableprefix}_id")] = simulation.Id;
                    entity[($"{tableprefix}_displayname")] = simulation.DisplayName;
                    entity[($"{tableprefix}_description")] = simulation.Description;
                    entity[($"{tableprefix}_attacktype")] = simulation.AttackType;
                    entity[($"{tableprefix}_payloaddeliveryplatform")] = simulation.PayloadDeliveryPlatform;
                    entity[($"{tableprefix}_attacktechnique")] = simulation.AttackTechnique;
                    entity[($"{tableprefix}_status")] = simulation.Status;
                    entity[($"{tableprefix}_createddatetime")] = simulation.CreatedDateTime;
                    entity[($"{tableprefix}_lastmodifieddatetime")] = simulation.LastModifiedDateTime;
                    entity[($"{tableprefix}_launchdatetime")] = simulation.LaunchDateTime;
                    entity[($"{tableprefix}_completiondatetime")] = simulation.CompletionDateTime;
                    entity[($"{tableprefix}_isautomated")] = simulation.IsAutomated;
                    entity[($"{tableprefix}_automationid")] = simulation.AutomationId;
                    entity[($"{tableprefix}_durationindays")] = simulation.DurationInDays;
                    entity[($"{tableprefix}_trainingsetting")] = simulation.TrainingSetting;
                    entity[($"{tableprefix}_oauthconsentappdetail")] = simulation.OAuthConsentAppDetail;
                    entity[($"{tableprefix}_endusernotificationsetting")] = simulation.EndUserNotificationSetting;
                    entity[($"{tableprefix}_includedaccounttarget")] = simulation.IncludedAccountTarget;
                    entity[($"{tableprefix}_excludedaccounttarget")] = simulation.ExcludedAccountTarget;
                    try
                    {
                        _logger.LogInformation($"Setting attribute createdby : Value type is {simulation.CreatedBy?.GetType()}");
                        entity[($"{tableprefix}_createdby")] = simulation.CreatedBy != null
                            ? $"email={simulation.CreatedBy.Email ?? "N/A"}; id={simulation.CreatedBy.Id ?? "N/A"}; displayName={simulation.CreatedBy.DisplayName ?? "N/A"}"
                            : "N/A";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error setting 'createdby': {ex.Message}");
                    }
                    try
                    {
                        _logger.LogInformation($"Setting attribute 'lastmodifiedby': Value type is {simulation.LastModifiedBy?.GetType()}");
                        entity[($"{tableprefix}_lastmodifiedby")] = simulation.LastModifiedBy != null
                            ? $"email={simulation.LastModifiedBy.Email ?? "N/A"}; id={simulation.LastModifiedBy.Id ?? "N/A"}; displayName={simulation.LastModifiedBy.DisplayName ?? "N/A"}"
                            : "N/A";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error setting 'lastmodifiedby': {ex.Message}");
                    }


                    if (existingRecord != null)
                    {
                        // Record exists, update it
                        entity.Id = existingRecord.Id; // Set the ID of the existing record
                        await serviceClient.UpdateAsync(entity);
                        _logger.LogInformation($"Updated record with ID: {simulationId}");
                    }
                    else
                    {
                        // Record does not exist, create a new one
                        await serviceClient.CreateAsync(entity);
                        _logger.LogInformation($"Created new record with ID: {simulationId}");
                    }
                }
                else
                {
                    throw new Exception($"Invalid ID format: {simulation.Id}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error writing to Dataverse: {ex.Message}");
            }
        }
        private static async Task WriteSimulationUsersToDataverse(ServiceClient serviceClient,SimulationUsers SimulationUsers,string SimulationUsersTable,string id,ILogger logger)
        {
            try
            {
                var tableprefix = Environment.GetEnvironmentVariable("tableprefix", EnvironmentVariableTarget.Process);

                var existingRecord = await RetrieveExistingRecordSimUser(
                    serviceClient,
                    SimulationUsersTable,
                    $"{tableprefix}_simulationuser",
                    JsonConvert.SerializeObject(SimulationUsers.simulationUser),
                    $"{tableprefix}_simulationid",
                    id);

                if (existingRecord != null)
                {
                    var entityToUpdate = new Entity(SimulationUsersTable)
                    {
                        Id = existingRecord.Id,
                        [$"{tableprefix}_iscompromised"] = SimulationUsers.isCompromised,
                        [$"{tableprefix}_simulationid"] = id,
                        [$"{tableprefix}_compromiseddatetime"] = SimulationUsers.compromisedDateTime ?? (DateTime?)null,
                        [$"{tableprefix}_assignedtrainingscount"] = SimulationUsers.assignedTrainingsCount,
                        [$"{tableprefix}_completedtrainingscount"] = SimulationUsers.completedTrainingsCount,
                        [$"{tableprefix}_inprogresstrainingscount"] = SimulationUsers.inProgressTrainingsCount,
                        [$"{tableprefix}_reportedphishdatetime"] = SimulationUsers.reportedPhishDateTime ?? (DateTime?)null,
                        [$"{tableprefix}_simulationuser"] = JsonConvert.SerializeObject(SimulationUsers.simulationUser),
                        [$"{tableprefix}_simulationevents"] = JsonConvert.SerializeObject(SimulationUsers.SimulationEvents),
                    };

                    await serviceClient.UpdateAsync(entityToUpdate);
                }
                else
                {
                    var entityToCreate = new Entity(SimulationUsersTable)
                    {
                        [$"{tableprefix}_iscompromised"] = SimulationUsers.isCompromised,
                        [$"{tableprefix}_simulationid"] = id,
                        [$"{tableprefix}_compromiseddatetime"] = SimulationUsers.compromisedDateTime ?? (DateTime?)null,
                        [$"{tableprefix}_assignedtrainingscount"] = SimulationUsers.assignedTrainingsCount,
                        [$"{tableprefix}_completedtrainingscount"] = SimulationUsers.completedTrainingsCount,
                        [$"{tableprefix}_inprogresstrainingscount"] = SimulationUsers.inProgressTrainingsCount,
                        [$"{tableprefix}_reportedphishdatetime"] = SimulationUsers.reportedPhishDateTime ?? (DateTime?)null,
                        [$"{tableprefix}_simulationuser"] = JsonConvert.SerializeObject(SimulationUsers.simulationUser),
                        [$"{tableprefix}_simulationevents"] = JsonConvert.SerializeObject(SimulationUsers.SimulationEvents),
                    };

                    await serviceClient.CreateAsync(entityToCreate);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to write simulation user for Simulation ID {id} / User: {JsonConvert.SerializeObject(SimulationUsers.simulationUser)}");
            }
        }

        private static async Task WriteTrainingUserCoverageToDataverse(ServiceClient serviceClient, TrainingUserCoverage TrainingUsers, string TrainingUserTable, ILogger logger)
        {
            var tableprefix = Environment.GetEnvironmentVariable("tableprefix", EnvironmentVariableTarget.Process);
            // Retrieve the existing record
            var existingRecord = await RetrieveExistingRecord(serviceClient, TrainingUserTable, ($"{tableprefix}_attacksimulationuser"), JsonConvert.SerializeObject(TrainingUsers.attackSimulationUser));

            if (existingRecord != null)
            {
                // Record exists, prepare the entity for update
                var entityToUpdate = new Entity(TrainingUserTable)
                {
                    Id = existingRecord.Id, // Set the ID of the existing record
                    [($"{tableprefix}_usertrainings")] = JsonConvert.SerializeObject(TrainingUsers.userTrainings),
                    [($"{tableprefix}_attacksimulationuser")] = JsonConvert.SerializeObject(TrainingUsers.attackSimulationUser),
                };

                // Perform the update
                await serviceClient.UpdateAsync(entityToUpdate);
                logger.LogInformation($"Updated user TrainingUserCoverage record for {TrainingUsers.attackSimulationUser.UserId}");
            }
            else
            {
                // Record does not exist, prepare the entity for creation
                var entityToCreate = new Entity(TrainingUserTable)
                {
                    [($"{tableprefix}_usertrainings")] = JsonConvert.SerializeObject(TrainingUsers.userTrainings),
                    [($"{tableprefix}_attacksimulationuser")] = JsonConvert.SerializeObject(TrainingUsers.attackSimulationUser),
                };

                // Perform the create operation
                await serviceClient.CreateAsync(entityToCreate);
                logger.LogInformation($"Created new user TrainingUserCoverage record for {TrainingUsers.attackSimulationUser.UserId}");
            }
        }
        private static async Task MarkSimulationAsProcessed(ServiceClient serviceClient, string simulationId, string SimulationTable, ILogger _logger)
        {
            var tableprefix = Environment.GetEnvironmentVariable("tableprefix", EnvironmentVariableTarget.Process);

            if (!Guid.TryParse(simulationId, out Guid simulationGuid))
            {
                _logger.LogError($"Invalid simulationId format: {simulationId}");
                return;
            }

            try
            {
                // Retrieve the record based on simulationId (not SyncStatus)
                var existingRecord = await RetrieveExistingRecord(serviceClient, SimulationTable, $"{tableprefix}_id", simulationId);

                if (existingRecord != null)
                {
                    // Check if it's already marked as completed
                    var currentStatus = existingRecord.Contains($"{tableprefix}_syncstatus")
                        ? existingRecord[$"{tableprefix}_syncstatus"]?.ToString()
                        : null;

                    if (string.Equals(currentStatus, "completed", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation($"Simulation {simulationId} already marked as processed. Skipping update.");
                        return;
                    }

                    var updateEntity = new Entity(SimulationTable)
                    {
                        Id = existingRecord.Id
                    };

                    updateEntity[$"{tableprefix}_syncstatus"] = "completed";

                    await serviceClient.UpdateAsync(updateEntity);
                    _logger.LogInformation($"Marked simulation {simulationId} as processed (SyncStatus = 'completed')");
                }
                else
                {
                    _logger.LogWarning($"Could not find simulation record with ID: {simulationId} to mark as processed.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to mark simulation {simulationId} as processed.");
            }
        }
        private static async Task<Dictionary<string, string>> RetrieveSyncStatusesForSimulations( ServiceClient serviceClient, string tableName, List<string> simulationIds, ILogger _logger)
        { 
            var tableprefix = Environment.GetEnvironmentVariable("tableprefix", EnvironmentVariableTarget.Process);
            var result = new Dictionary<string, string>();

            foreach (var simId in simulationIds)
            {
                try
                {
                    var existingRecord = await RetrieveExistingRecord(serviceClient, tableName, $"{tableprefix}_id", simId);
                    if (existingRecord != null && existingRecord.Contains($"{tableprefix}_syncstatus"))
                    {
                        result[simId] = existingRecord[$"{tableprefix}_syncstatus"]?.ToString();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to retrieve SyncStatus for simulation {simId}");
                }
            }

            return result;
        }

        public class SimulationResponse
        {
            [JsonProperty("value")]
            public List<Simulation> Value { get; set; }

            [JsonProperty("@odata.nextLink")]
            public string NextLink { get; set; }
        }

        public class UserCoverageResponse
        {
            [JsonProperty("value")]
            public List<UserCoverage> Value { get; set; }

            [JsonProperty("@odata.nextLink")]
            public string NextLink { get; set; }
        }

        public class TrainingUserCoverageResponse
        {
            [JsonProperty("value")]
            public List<TrainingUserCoverage> Value { get; set; }

            [JsonProperty("@odata.nextLink")]
            public string NextLink { get; set; }
        }

        public class SimulationUsersResponse
        {
            [JsonProperty("value")]
            public List<SimulationUsers> Value { get; set; }

            [JsonProperty("@odata.nextLink")]
            public string NextLink { get; set; }
        }

        public class UserCoverage
        {
            public int SimulationCount { get; set; }
            public DateTime? LatestSimulationDateTime { get; set; }
            public int ClickCount { get; set; }
            public int CompromisedCount { get; set; }
            public AttackSimulationUserDetails AttackSimulationUser { get; set; }

            public class AttackSimulationUserDetails
            {
                public string Email { get; set; }
                public string UserId { get; set; }
                public string DisplayName { get; set; }
            }
        }

        public class TrainingUserCoverage
        {
            public List<userTrainingsDetails> userTrainings { get; set; }

            public class userTrainingsDetails
            {
                public DateTime? AssignedDateTime { get; set; }
                public DateTime? CompletionDateTime { get; set; }
                public string TrainingStatus { get; set; }
                public string DisplayName { get; set; }
            }

            public attackSimulationUserDetails attackSimulationUser { get; set; }

            public class attackSimulationUserDetails
            {
                public string UserId { get; set; }
                public string DisplayName { get; set; }
                public string Email { get; set; }
            }
        }

        public class Simulation
        {
            public string Id { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string AttackType { get; set; }
            public string PayloadDeliveryPlatform { get; set; }
            public string AttackTechnique { get; set; }
            public string Status { get; set; }
            public DateTime CreatedDateTime { get; set; }
            public DateTime LastModifiedDateTime { get; set; }
            public DateTime? LaunchDateTime { get; set; }
            public DateTime? CompletionDateTime { get; set; }
            public bool IsAutomated { get; set; }
            public string AutomationId { get; set; }
            public int DurationInDays { get; set; }
            public string TrainingSetting { get; set; }
            public string OAuthConsentAppDetail { get; set; }
            public string EndUserNotificationSetting { get; set; }
            public string IncludedAccountTarget { get; set; }
            public string ExcludedAccountTarget { get; set; }
            public string SyncStatus { get; set; }
            public CreatedOrModifiedBy CreatedBy { get; set; }
            public CreatedOrModifiedBy LastModifiedBy { get; set; }

            public class CreatedOrModifiedBy
            {
                public string Email { get; set; }
                public string Id { get; set; }
                public string DisplayName { get; set; }
            }
        }

        public class SimulationUsers
        {
            public string isCompromised { get; set; }
            public string SimulationID { get; set; }
            public DateTime? compromisedDateTime { get; set; }
            public int assignedTrainingsCount { get; set; }
            public int completedTrainingsCount { get; set; }
            public int inProgressTrainingsCount { get; set; }
            public DateTime? reportedPhishDateTime { get; set; }
            public SimulationUserDetails simulationUser { get; set; }

            public class SimulationUserDetails
            {
                public string UserId { get; set; }
                public string DisplayName { get; set; }
                public string Email { get; set; }
            }
            public List<SimulationEventsDetails> SimulationEvents { get; set; }

            public class SimulationEventsDetails
            {
                public string eventName { get; set; }
                public DateTime? eventDateTime { get; set; }
                public string ipAddress { get; set; }
                public string osPlatformDeviceDetails { get; set; }
                public string browser { get; set; }
                public string clickSource { get; set; }
            }
        }
    }
}
