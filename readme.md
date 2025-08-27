# Function1.cs - Azure Function for Simulation Data Synchronization

## Overview
`Function1.cs` is an Azure Function designed to automate the synchronization of simulation data between Microsoft Graph API and Microsoft Dataverse. It processes simulation data, user coverage, and training user coverage, ensuring efficient and reliable data handling.

---

## Key Features

### 1. Timer Trigger
The function is triggered periodically using a __TimerTrigger__. The current schedule is set to run every hour:

### 2. Environment Variables
The function retrieves configuration values from environment variables to connect to external services. Examples include:
- `TenantId`
- `ClientId`
- `DataverseConnection`
- `SimulationTable`
- `CoverageUsersTable`
- `SimulationUsersTable`
- `TrainingUserTable`

### 3. Microsoft Graph API Integration
The function interacts with Microsoft Graph API to:
- Fetch simulations (`/security/attackSimulation/simulations`).
- Retrieve user coverage and training user coverage data.

### 4. Dataverse Integration
The function uses the `ServiceClient` to interact with Microsoft Dataverse, performing CRUD operations on tables like:
- `SimulationTable`
- `SimulationUsersTable`
- `CoverageUsersTable`
- `TrainingUserTable`

---

## Workflow

### Step 1: Fetch Microsoft Graph Endpoints
The function determines the appropriate Microsoft Graph base URL based on the environment (e.g., `AzureUSDoD`, `AzureGov`, or default `graph.microsoft.com`).

### Step 2: Acquire Access Token
The `GetAccessToken` method retrieves an OAuth token for authenticating requests to Microsoft Graph API. It caches the token and refreshes it before expiration.

### Step 3: Fetch Simulations
The `GetSimulations` method retrieves a list of simulations from Microsoft Graph API. It handles pagination and rate-limiting (HTTP 429).

### Step 4: Write Simulations to Dataverse
The `WriteSimulationToDataverse` method writes simulation data to the `SimulationTable` in Dataverse. It checks if a record already exists and either updates or creates it.

### Step 5: Synchronize Simulation Status
The function retrieves the sync status of simulations from Dataverse and filters simulations based on their status and sync rules. It processes simulations that:
- Are completed but not yet marked as "Completed."
- Are running but haven't been synced in the last 24 hours.
- Are canceled or excluded but not yet marked as "Completed."

### Step 6: Process Simulation Users
For each simulation, the function:
1. Fetches associated users using `GetSimulationUsers`.
2. Writes user data to the `SimulationUsersTable` in Dataverse using `WriteSimulationUsersToDataverse`.

### Step 7: Fetch and Write Training User Coverage
The function retrieves training user coverage data from Microsoft Graph API and writes it to the `TrainingUserTable` in Dataverse using `WriteTrainingUserCoverageToDataverse`.

### Step 8: Handle User Coverage (Commented Out)
The code includes commented-out sections for fetching and writing user coverage data to Dataverse. These operations are marked as long-running processes.

---

## Error Handling
The function includes robust error handling:
- Logs errors using `ILogger`.
- Retries requests on rate-limiting (HTTP 429) or token expiration (HTTP 401).
- Catches and logs exceptions during data processing.

---

## Supporting Methods
The file includes several helper methods for specific tasks:
- **`RetrieveExistingRecord`**: Checks if a record exists in Dataverse.
- **`MarkSimulationAsProcessed`**: Updates the sync status of a simulation.
- **`AddUserCountToSimulation`**: Updates the user count for a simulation.
- **`RetrieveSyncStatusesForSimulations`**: Retrieves sync statuses for multiple simulations.

---

## Data Models
The file defines data models for deserializing API responses:
- **`Simulation`**: Represents a simulation.
- **`UserCoverage`**: Represents user coverage data.
- **`TrainingUserCoverage`**: Represents training user coverage data.
- **`SimulationUsers`**: Represents users associated with a simulation.

---

## Observations
- **Concurrency**: The function uses `Task.WhenAll` to process simulations and users concurrently.
- **Rate Limiting**: The function handles rate-limiting by respecting the `Retry-After` header or applying exponential backoff.
- **Pagination**: The function handles paginated responses from Microsoft Graph API using the `@odata.nextLink` property.

---

## Summary
The function automates the synchronization of simulation data between Microsoft Graph API and Dataverse. It is designed to handle large datasets, rate-limiting, and token expiration gracefully, ensuring reliable and efficient data processing.
