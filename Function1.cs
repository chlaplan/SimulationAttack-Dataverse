using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
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

namespace SimulationAttack_Dataverse
{
    public class Function1
    {
        private readonly ILogger _logger;
        private static readonly HttpClient client = new HttpClient();
        private static readonly HttpClient _httpClient = new HttpClient();

        public Function1(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<Function1>();
        }

        [Function("MDO-SimulationData-Dataverse")]
        public async Task Run([TimerTrigger("0 0 6 * * *")] TimerInfo myTimer)
        {
            _logger.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            if (myTimer.ScheduleStatus is not null)
            {
                _logger.LogInformation($"Next timer schedule at: {myTimer.ScheduleStatus.Next}");
            }

            try
            {
                string tenantId = Environment.GetEnvironmentVariable("TenantID");
                string clientId = Environment.GetEnvironmentVariable("ClientID");
                string clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
                string environmentName = Environment.GetEnvironmentVariable("EnvironmentName") ?? "Global";
                string DataverseConnection = Environment.GetEnvironmentVariable("DataverseConnection");
                string SimulationTable = Environment.GetEnvironmentVariable("SimulationTable");
                string CoverageUsersTable = Environment.GetEnvironmentVariable("CoverageUsersTable"); // Table for user coverage
                string SimulationUsersTable = Environment.GetEnvironmentVariable("SimulationUsersTable"); // Table for SimulationUsersTable
                string TrainingUserTable = Environment.GetEnvironmentVariable("TrainingUserTable"); // Table for SimulationUsersTable

                // Step 1: Get Microsoft Graph Endpoints
                var targetCloud = Environment.GetEnvironmentVariable("AzureEnvironment");
                string graphBaseUrl;

                switch (targetCloud)
                {
                    case "AzureUSDoD":
                        graphBaseUrl = "https://dod-graph.microsoft.us/";
                        break;
                    case "AzureGov":
                        graphBaseUrl = "https://graph.microsoft.us/";
                        break;
                    default:
                        graphBaseUrl = "https://graph.microsoft.com/";
                        break;
                }

                // Step 2: Get access token for Microsoft Graph
                var accessToken = await GetAccessToken(graphBaseUrl, _logger);

                // Step 3: Fetch simulations
                var simulations = await GetSimulations(graphBaseUrl, accessToken);

                // Step 4: Fetch user coverage data
                var userCoverage = await GetAllAttackSimulationUserCoverage(graphBaseUrl, accessToken);

                // Making Connection to Dataverse
                var dataverseConnection = Environment.GetEnvironmentVariable("DataverseConnection");
                if (string.IsNullOrEmpty(dataverseConnection))
                {
                    throw new ArgumentException("Dataverse connection string is not set or is invalid.");
                }
                var serviceClient = new ServiceClient(dataverseConnection);

                // Step 5: Write coverage data to Dataverse
                foreach (var user in userCoverage)
                 {
                    await WriteUserCoverageToDataverse(serviceClient, user, CoverageUsersTable, _logger);
                 }

                // Step 6: Write simulations to Dataverse
                foreach (var simulation in simulations)
                {
                    await WriteSimulationToDataverse(serviceClient, simulation, SimulationTable, _logger);
                }

                // Step 7: Get GetSimulationUsers
                foreach (var simulation in simulations)
                {
                    var simulationUsersList = await GetSimulationUsers(graphBaseUrl, accessToken, simulation.Id);
                    foreach (var user in simulationUsersList)
                    {
                        await WriteSimulationUsersToDataverse(serviceClient, user, SimulationUsersTable, _logger);
                    }
                }

                // Step 8: Fetch TrainingUserCoverage data
                var TrainingUserCoverage = await GetTrainingUserCoverage(graphBaseUrl, accessToken);

                // Step 9: Write TrainingUserCoverage to Dataverse
                foreach (var TrainingUsers in TrainingUserCoverage)
                {
                    await WriteTrainingUserCoverageToDataverse(serviceClient, TrainingUsers, TrainingUserTable, _logger);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"An error occurred: {ex.Message}");
            }
        }

        private static async Task<string> GetAccessToken(string uri, ILogger logger)
        {
            var clientSecret = Environment.GetEnvironmentVariable("clientSecret", EnvironmentVariableTarget.Process);
            var clientId = Environment.GetEnvironmentVariable("clientId", EnvironmentVariableTarget.Process);
            var TenantId = Environment.GetEnvironmentVariable("TenantId", EnvironmentVariableTarget.Process);
            var TargetCloud = Environment.GetEnvironmentVariable("AzureEnvironment", EnvironmentVariableTarget.Process);

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

            // Log the full request details, used for troublshooting
            logger.LogInformation($"Token request URI: {tokenUri}");
            logger.LogInformation($"Token request content: {string.Join(", ", tokenRequestContent.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");

            try
            {
                var tokenResponse = await _httpClient.SendAsync(tokenRequest);
                tokenResponse.EnsureSuccessStatusCode();
                var tokenContent = await tokenResponse.Content.ReadAsStringAsync();
                var tokenData = JsonConvert.DeserializeObject<dynamic>(tokenContent);
                return tokenData.access_token;
            }
            catch (Exception ex)
            {
                var errorMessage = $"Error getting access token for URI {tokenUri}: {ex.Message}";
                logger.LogError(errorMessage);
                throw new InvalidOperationException(errorMessage);
            }
        }
        private static async Task<List<UserCoverage>> GetAllAttackSimulationUserCoverage(string graphBaseUrl, string accessToken)
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var allUserCoverage = new List<UserCoverage>();
            string nextLink = $"{graphBaseUrl}/v1.0/reports/security/getAttackSimulationSimulationUserCoverage";

            do
            {
                var response = await client.GetStringAsync(nextLink);
                var userCoverageResponse = JsonConvert.DeserializeObject<UserCoverageResponse>(response);

                // Add the current page of user coverage values to the list
                allUserCoverage.AddRange(userCoverageResponse.Value);

                // Check for the next page
                nextLink = userCoverageResponse.NextLink;

                if (nextLink != null)
                {
                    // Wait for 5 seconds before the next request
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }

            } while (nextLink != null);

            return allUserCoverage;
        }

        private static async Task<List<TrainingUserCoverage>> GetTrainingUserCoverage(string graphBaseUrl, string accessToken)
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var allTrainingUserCoverage = new List<TrainingUserCoverage>();
            string nextLink = $"{graphBaseUrl}/v1.0/reports/security/getAttackSimulationTrainingUserCoverage";

            do
            {
                var response = await client.GetStringAsync(nextLink);
                var TrainingUserCoverageResponse = JsonConvert.DeserializeObject<TrainingUserCoverageResponse>(response);

                // Add the current page of user coverage values to the list
                allTrainingUserCoverage.AddRange(TrainingUserCoverageResponse.Value);

                // Check for the next page
                nextLink = TrainingUserCoverageResponse.NextLink;

                if (nextLink != null)
                {
                    // Wait for 5 seconds before the next request
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }

            } while (nextLink != null);

            return allTrainingUserCoverage;
        }

        private static async Task<List<Simulation>> GetSimulations(string graphBaseUrl, string accessToken)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await client.GetStringAsync($"{graphBaseUrl}/v1.0/security/attackSimulation/simulations");
            var simulations = JsonConvert.DeserializeObject<SimulationResponse>(response);
            return simulations.Value;
        }

        private static async Task<List<SimulationUsers>> GetSimulationUsers(string graphBaseUrl, string accessToken, string id)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var allSimulationUsers = new List<SimulationUsers>();
            string nextLink = $"{graphBaseUrl}/v1.0/security/attackSimulation/simulations/{id}/report/simulationUsers";

            do
            {
                // Make the HTTP request to get simulation users
                var response = await client.GetStringAsync(nextLink);
                var simulationUsersResponse = JsonConvert.DeserializeObject<SimulationUsersResponse>(response);

                // Add the current page of user data to the list
                allSimulationUsers.AddRange(simulationUsersResponse.Value);

                // Check for the next page
                nextLink = simulationUsersResponse.NextLink; // Match this to the actual JSON property name

                // Log page retrieval (optional)
                Console.WriteLine($"Retrieved page with {simulationUsersResponse.Value.Count} records.");

                if (nextLink != null)
                {
                    // Wait for 5 seconds before the next request to avoid throttling
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }

            } while (nextLink != null);

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

        private static async Task WriteUserCoverageToDataverse(ServiceClient serviceClient, UserCoverage user, string CoverageUsersTable, ILogger logger)
        {
            // Log the check for existing records
            // logger.LogInformation($"Checking for existing record in {CoverageUsersTable} where crfac_attacksimulationuser = {JsonConvert.SerializeObject(user.AttackSimulationUser.UserId)}");

            // Retrieve the existing record
            var existingRecord = await RetrieveExistingRecord(serviceClient, CoverageUsersTable, "crfac_attacksimulationuser", JsonConvert.SerializeObject(user.AttackSimulationUser));

            if (existingRecord != null)
            {
                // Record exists, prepare the entity for update
                var entityToUpdate = new Entity(CoverageUsersTable)
                {
                    Id = existingRecord.Id, // Set the ID of the existing record
                    ["crfac_simulationcount"] = user.SimulationCount,
                    ["crfac_latestsimulationdatetime"] = user.LatestSimulationDateTime ?? (DateTime?)null,
                    ["crfac_clickcount"] = user.ClickCount,
                    ["crfac_compromisedcount"] = user.CompromisedCount,
                    ["crfac_attacksimulationuser"] = JsonConvert.SerializeObject(user.AttackSimulationUser),
                };

                // Perform the update
                await serviceClient.UpdateAsync(entityToUpdate);
                logger.LogInformation($"Updated user coverage record for {user.AttackSimulationUser.UserId}");
            }
            else
            {
                // Record does not exist, prepare the entity for creation
                var entityToCreate = new Entity(CoverageUsersTable)
                {
                    ["crfac_simulationcount2"] = user.SimulationCount,
                    ["crfac_latestsimulationdatetime"] = user.LatestSimulationDateTime ?? (DateTime?)null,
                    ["crfac_clickcount"] = user.ClickCount,
                    ["crfac_compromisedcount"] = user.CompromisedCount,
                    ["crfac_attacksimulationuser"] = JsonConvert.SerializeObject(user.AttackSimulationUser),
                };

                // Perform the create operation
                await serviceClient.CreateAsync(entityToCreate);
                logger.LogInformation($"Created new user coverage record for {user.AttackSimulationUser.UserId}");
            }
        }

        private static async Task WriteSimulationToDataverse(ServiceClient serviceClient, Simulation simulation, string SimulationTable, ILogger _logger)
        {
            // Log the table name for verification
            _logger.LogInformation($"Writing to Dataverse table: {SimulationTable}");

            try
            {
                // Convert simulation.Id from string to Guid
                if (Guid.TryParse(simulation.Id, out Guid simulationId))
                {
                    // Attempt to retrieve the existing record
                    var existingRecord = await RetrieveExistingRecord(serviceClient, SimulationTable, "crfac_id", simulation.Id);

                    // Create the entity to be created or updated
                    var entity = new Entity(SimulationTable);

                    // Set the common fields
                    entity["crfac_id"] = simulation.Id;
                    entity["crfac_displayname"] = simulation.DisplayName;
                    entity["crfac_description"] = simulation.Description;
                    entity["crfac_attacktype"] = simulation.AttackType;
                    entity["crfac_payloaddeliveryplatform"] = simulation.PayloadDeliveryPlatform;
                    entity["crfac_attacktechnique"] = simulation.AttackTechnique;
                    entity["crfac_status"] = simulation.Status;
                    entity["crfac_createddatetime"] = simulation.CreatedDateTime;
                    entity["crfac_lastmodifieddatetime"] = simulation.LastModifiedDateTime;
                    entity["crfac_launchdatetime"] = simulation.LaunchDateTime;
                    entity["crfac_completiondatetime"] = simulation.CompletionDateTime;
                    entity["crfac_isautomated"] = simulation.IsAutomated;
                    entity["crfac_automationid"] = simulation.AutomationId;
                    entity["crfac_durationindays"] = simulation.DurationInDays;
                    entity["crfac_trainingsetting"] = simulation.TrainingSetting;
                    entity["crfac_oauthconsentappdetail"] = simulation.OAuthConsentAppDetail;
                    entity["crfac_endusernotificationsetting"] = simulation.EndUserNotificationSetting;
                    entity["crfac_includedaccounttarget"] = simulation.IncludedAccountTarget;
                    entity["crfac_excludedaccounttarget"] = simulation.ExcludedAccountTarget;
                    try
                    {
                        _logger.LogInformation($"Setting attribute 'crfac_createdby': Value type is {simulation.CreatedBy?.GetType()}");
                        entity["crfac_createdby"] = simulation.CreatedBy != null
                            ? $"email={simulation.CreatedBy.Email ?? "N/A"}; id={simulation.CreatedBy.Id ?? "N/A"}; displayName={simulation.CreatedBy.DisplayName ?? "N/A"}"
                            : "N/A";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error setting 'crfac_createdby': {ex.Message}");
                    }
                    try
                    {
                        _logger.LogInformation($"Setting attribute 'crfac_lastmodifiedby': Value type is {simulation.LastModifiedBy?.GetType()}");
                        entity["crfac_lastmodifiedby"] = simulation.LastModifiedBy != null
                            ? $"email={simulation.LastModifiedBy.Email ?? "N/A"}; id={simulation.LastModifiedBy.Id ?? "N/A"}; displayName={simulation.LastModifiedBy.DisplayName ?? "N/A"}"
                            : "N/A";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error setting 'crfac_lastmodifiedby': {ex.Message}");
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

        private static async Task WriteSimulationUsersToDataverse(ServiceClient serviceClient, SimulationUsers SimulationUsers, string SimulationUsersTable, ILogger logger)
        {

            // Retrieve the existing record
            var existingRecord = await RetrieveExistingRecord(serviceClient, SimulationUsersTable, "crfac_simulationuser", JsonConvert.SerializeObject(SimulationUsers.simulationUser));

            if (existingRecord != null)
            {
                // Record exists, prepare the entity for update
                var entityToUpdate = new Entity(SimulationUsersTable)
                {
                    Id = existingRecord.Id, // Set the ID of the existing record
                    ["crfac_iscompromised"] = SimulationUsers.isCompromised,
                    ["crfac_compromiseddatetime"] = SimulationUsers.compromisedDateTime ?? (DateTime?)null,
                    ["crfac_assignedtrainingscount"] = SimulationUsers.assignedTrainingsCount,
                    ["crfac_completedtrainingscount"] = SimulationUsers.completedTrainingsCount,
                    ["crfac_inprogresstrainingscount"] = SimulationUsers.inProgressTrainingsCount,
                    ["crfac_reportedphishdatetime"] = SimulationUsers.reportedPhishDateTime ?? (DateTime?)null,
                    ["crfac_simulationuser"] = JsonConvert.SerializeObject(SimulationUsers.simulationUser),
                    ["crfac_simulationevents"] = JsonConvert.SerializeObject(SimulationUsers.SimulationEvents),
                };

                // Perform the update
                await serviceClient.UpdateAsync(entityToUpdate);
                logger.LogInformation($"Updated user SimulationUsers record for {SimulationUsers.simulationUser.UserId}");
            }
            else
            {
                // Record does not exist, prepare the entity for creation
                var entityToCreate = new Entity(SimulationUsersTable)
                {
                    ["crfac_iscompromised"] = SimulationUsers.isCompromised,
                    ["crfac_compromiseddatetime"] = SimulationUsers.compromisedDateTime ?? (DateTime?)null,
                    ["crfac_assignedtrainingscount"] = SimulationUsers.assignedTrainingsCount,
                    ["crfac_completedtrainingscount"] = SimulationUsers.completedTrainingsCount,
                    ["crfac_inprogresstrainingscount"] = SimulationUsers.inProgressTrainingsCount,
                    ["crfac_reportedphishdatetime"] = SimulationUsers.reportedPhishDateTime ?? (DateTime?)null,
                    ["crfac_simulationuser"] = JsonConvert.SerializeObject(SimulationUsers.simulationUser),
                    ["crfac_simulationevents"] = JsonConvert.SerializeObject(SimulationUsers.SimulationEvents),
                };

                // Perform the create operation
                await serviceClient.CreateAsync(entityToCreate);
                logger.LogInformation($"Created new user SimulationUsers record for {SimulationUsers.simulationUser.UserId}");
            }
        }

        private static async Task WriteTrainingUserCoverageToDataverse(ServiceClient serviceClient, TrainingUserCoverage TrainingUsers, string SimulationUsersTable, ILogger logger)
        {

            // Retrieve the existing record
            var existingRecord = await RetrieveExistingRecord(serviceClient, SimulationUsersTable, "crfac_attacksimulationuser", JsonConvert.SerializeObject(TrainingUsers.attackSimulationUser));

            if (existingRecord != null)
            {
                // Record exists, prepare the entity for update
                var entityToUpdate = new Entity(SimulationUsersTable)
                {
                    Id = existingRecord.Id, // Set the ID of the existing record
                    ["crfac_usertrainings"] = JsonConvert.SerializeObject(TrainingUsers.userTrainings),
                    ["crfac_attacksimulationuser"] = JsonConvert.SerializeObject(TrainingUsers.attackSimulationUser),
                };

                // Perform the update
                await serviceClient.UpdateAsync(entityToUpdate);
                logger.LogInformation($"Updated user TrainingUserCoverage record for {TrainingUsers.attackSimulationUser.UserId}");
            }
            else
            {
                // Record does not exist, prepare the entity for creation
                var entityToCreate = new Entity(SimulationUsersTable)
                {
                    ["crfac_usertrainings"] = JsonConvert.SerializeObject(TrainingUsers.userTrainings),
                    ["crfac_attacksimulationuser"] = JsonConvert.SerializeObject(TrainingUsers.attackSimulationUser),
                };

                // Perform the create operation
                await serviceClient.CreateAsync(entityToCreate);
                logger.LogInformation($"Created new user TrainingUserCoverage record for {TrainingUsers.attackSimulationUser.UserId}");
            }
        }


        public class SimulationResponse
        {
            public List<Simulation> Value { get; set; }
        }

        public class UserCoverageResponse
        {
            public List<UserCoverage> Value { get; set; }
            public string NextLink { get; set; }
        }

        public class TrainingUserCoverageResponse
        {
            public List<TrainingUserCoverage> Value { get; set; }
            public string NextLink { get; set; }
        }

        public class SimulationUsersResponse
        {
            public List<SimulationUsers> Value { get; set; }
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
