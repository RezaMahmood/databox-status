using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Management.DataBox;
using Azure.Identity;
using Microsoft.Rest;
using System.Linq;
using Microsoft.Azure.Management.DataBox.Models;
using Newtonsoft.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;
using System.Threading.Tasks;
using System.Collections.Generic;


namespace databox_status
{
    public class CheckDataboxStatus
    {
        /// <summary>
        /// Runs every hour
        /// </summary>
        /// <param name="myTimer"></param>
        /// <param name="log"></param>
        [FunctionName("CheckDataboxStatus")]
        public async Task Run([TimerTrigger("0 */2 * * * *")] TimerInfo myTimer, ILogger log)
        {
            var subscriptionId = System.Environment.GetEnvironmentVariable("subscriptionid", EnvironmentVariableTarget.Process);
            var credential = new ManagedIdentityCredential();

            var token = credential.GetToken(new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));
            var accessToken = token.Token;
            using (var databoxClient = new DataBoxManagementClient(new TokenCredentials(accessToken)))
            {
                databoxClient.SubscriptionId = subscriptionId;
                var jobs = databoxClient.Jobs.List().ToList<JobResource>(); // Lists all the jobs in the subscription

                foreach (var job in jobs)
                {
                    // JobResource reference: https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.management.databox.models.jobresource?view=azure-dotnet
                    var jobName = job.Name;
                    var resourceGroup = GetResourceGroup(job.Id);

                    var jobItem = await databoxClient.Jobs.GetAsync(resourceGroup, jobName, "details");
                    var databoxDiskJobDetails = (DataBoxDiskJobDetails)jobItem.Details;

                    var latestJobStage = databoxDiskJobDetails.JobStages.OrderByDescending(x => x.StageTime).FirstOrDefault();

                    if (latestJobStage != null)
                    {
                        var jobStage = latestJobStage.StageName;
                        var jobStatus = latestJobStage.StageStatus;
                        var jobStageTime = latestJobStage.StageTime;

                        // One approach can be to store job stages into a database for reporting.  The other could be to use Azure Portal to interrogate stage based on Tags

                        // Still need to storage stage/status state somewhere. Check whether status is new

                        var isUpdatedStatus = await IsStatusNew(job.Id, jobStage, jobStatus, jobStageTime, log);


                        if (isUpdatedStatus && jobStatus != null && jobStatus != StageStatus.None)
                        {
                            HandleCurrentStatus(jobName, job.Id, jobStage, jobStatus, log);
                        }

                    }

                    log.LogInformation(JsonConvert.SerializeObject(jobs).ToString());
                }
            }

        }

        private async Task<bool> IsStatusNew(string jobId, string currentJobStage, StageStatus? currentJobStatus, DateTime? jobStageTime, ILogger log)
        {
            bool isNewStatus = false;
            // get the current status for this order (if it exists)

            var cosmosAccount = System.Environment.GetEnvironmentVariable("CosmosAccount", EnvironmentVariableTarget.Process);
            var cosmosDatabase = System.Environment.GetEnvironmentVariable("CosmosDatabase", EnvironmentVariableTarget.Process);
            var cosmosContainer = System.Environment.GetEnvironmentVariable("CosmosContainer", EnvironmentVariableTarget.Process);

            var cosmosClient = new CosmosClient(cosmosAccount, new ManagedIdentityCredential());
            var container = cosmosClient.GetContainer(cosmosDatabase, cosmosContainer);

            var cosmosjobId = Utils.GetCosmosIdFromJobId(jobId);

            log.LogInformation("Job id (for cosmos) is: " + jobId);

            var query = new QueryDefinition("SELECT * FROM status s where s.id = @id").WithParameter("@id", cosmosjobId);
            var results = new List<OrderStatus>();
            using (var resultsIterator = container.GetItemQueryIterator<OrderStatus>(query, requestOptions: new QueryRequestOptions() { PartitionKey = new PartitionKey(cosmosjobId) }))
            {
                while (resultsIterator.HasMoreResults)
                {
                    var response = await resultsIterator.ReadNextAsync();
                    results.AddRange(response);
                }
            }

            if (results.Any())
            {
                var orderstatus = results.First();
                log.LogInformation("cosmos document found: " + orderstatus.ToString());
                var previousStage = orderstatus.Stage;
                var previousStageStatus = orderstatus.Status;
                if (previousStage != currentJobStage || previousStageStatus != currentJobStatus.ToString())
                {
                    isNewStatus = true;
                }
            }

            // Upsert the record
            var upsertStatus = await container.UpsertItemAsync<OrderStatus>(new OrderStatus() { Id = jobId, Status = currentJobStatus.ToString(), LastUpdated = jobStageTime, Stage = currentJobStage }, new PartitionKey(jobId));

            if (upsertStatus.StatusCode != System.Net.HttpStatusCode.OK)
            {
                log.LogInformation("Upsert failed: " + upsertStatus.ToString());
            }

            return isNewStatus;
        }

        private void HandleCurrentStatus(string jobName, string jobId, string jobStage, StageStatus? jobStatus, ILogger log)
        {
            // Status reference: https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.management.databox.models.jobresource.status?view=azure-dotnet#microsoft-azure-management-databox-models-jobresource-status

            // Can handle different stages - for this example we are handling DataCopy
            if (jobStage == "DataCopy")
            {
                switch (jobStatus)
                {
                    case StageStatus.Succeeded:
                    case StageStatus.SucceededWithErrors:
                    case StageStatus.SucceededWithWarnings:
                        HandleCompleted(jobName, jobStage, jobStatus, jobId);
                        break;
                    case StageStatus.Cancelled:
                    case StageStatus.Failed:
                        NotifyFailed(jobName, jobStage, jobStatus, jobId);
                        break;
                    default:
                        log.LogInformation("Latest job: " + jobId + "  stage is: " + jobStage + " status is: " + jobStatus);
                        break;
                }
            }
        }

        private string GetResourceGroup(string resourceId)
        {
            // example Id would be: /subscriptions/xxxx/resourcegroups/yyyy/providers/Microsoft.DataBox/jobs/zzzz
            var splitResource = resourceId.Split("/");
            var resourceGroup = splitResource[4];

            return resourceGroup;
        }

        /// <summary>
        /// Handling errors or warnings could take time and requires retrieving more details than is provided by List operation.  Rather than blocking, we push to a queue to let something else process
        /// </summary>
        /// <param name="ordername"></param>
        /// <param name="status"></param>
        /// <param name="resourceId">Needed to get ResourceGroup of databox order to get order details like error details</param>
        private async void HandleCompleted(string ordername, string stage, StageStatus? status, string resourceId)
        {

            var serviceBusFQDN = System.Environment.GetEnvironmentVariable("ServiceBusFQDN", EnvironmentVariableTarget.Process);
            var client = new ServiceBusClient(serviceBusFQDN, new ManagedIdentityCredential());
            var sender = client.CreateSender(status.ToString());

            var message = JsonConvert.SerializeObject(new DataBoxStatus { OrderName = ordername, Stage = stage, Status = status.ToString(), ResourceGroup = GetResourceGroup(resourceId) });

            await sender.SendMessageAsync(new ServiceBusMessage(message));
        }

        private void NotifyFailed(string jobName, string jobStage, StageStatus? jobStatus, string jobId)
        {
            // this could make a call to a Logic App to send email notifications with O365 connector
        }

        // private CarDetails ParseCarDetails(string jobName)
        // {
        //     // assumes naming convention of YYYYMMDD_CountryCode_CarId_OrderNumber
        //     var jobstring = jobName.Split('_');
        //     var carDetails = new CarDetails()
        //     {
        //         CountryCode = jobstring[1],
        //         CarId = jobstring[2],
        //         OrderNumber = Convert.ToInt32(jobstring[3])
        //     };

        //     return carDetails;
        // }

        // private class CarDetails
        // {
        //     public string CarId { get; set; }
        //     public string CountryCode { get; set; }
        //     public int OrderNumber { get; set; }
        // }

        private class OrderStatus
        {
            [Newtonsoft.Json.JsonProperty(PropertyName = "id")]
            public string Id { get; set; }
            public string Stage { get; set; }
            public string Status { get; set; }
            public DateTime? LastUpdated { get; set; }
        }
    }
}
