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
        public async Task Run([TimerTrigger("0 */1 * * * *")] TimerInfo myTimer, ILogger log)
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
                    
                    // Log order details into a database if you wish to create a dashboard
                    // Check whether status is new
                    var isUpdatedStatus = await IsStatusNew(job, log);

                    // No need to handle the status if this is a brand new order
                    if (isUpdatedStatus)
                    {
                        HandleCurrentStatus(job, log);
                    }

                    log.LogInformation(JsonConvert.SerializeObject(jobs).ToString());
                }
            }

            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");
        }

        private async Task<bool> IsStatusNew(JobResource job, ILogger log)
        {
            bool isNewStatus = false;
            // get the current status for this order (if it exists)

            var cosmosAccount = System.Environment.GetEnvironmentVariable("CosmosAccount", EnvironmentVariableTarget.Process);
            var cosmosDatabase = System.Environment.GetEnvironmentVariable("CosmosDatabase", EnvironmentVariableTarget.Process);
            var cosmosContainer = System.Environment.GetEnvironmentVariable("CosmosContainer", EnvironmentVariableTarget.Process);

            var cosmosClient = new CosmosClient(cosmosAccount, new ManagedIdentityCredential());
            var container = cosmosClient.GetContainer(cosmosDatabase, cosmosContainer);

            var jobId = Utils.GetCosmosIdFromJobId(job.Id);

            log.LogInformation("Job id (for cosmos) is: " + jobId);

            var query = new QueryDefinition("SELECT * FROM status s where s.id = @id").WithParameter("@id", jobId);
            var results = new List<OrderStatus>();
            using (var resultsIterator = container.GetItemQueryIterator<OrderStatus>(query, requestOptions: new QueryRequestOptions() { PartitionKey = new PartitionKey(jobId) }))
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
                var previousstatus = orderstatus.Status;
                if (previousstatus != job.Status)
                {
                    isNewStatus = true;
                }
            }

            // Get the time for the latest status
            var latestStage = job.Details.JobStages.OrderByDescending(x => x.StageTime).FirstOrDefault().StageTime;
            log.LogInformation("Latest stage time of the job was: " + latestStage.ToString());

            // Upsert the record
            var upsertStatus = await container.UpsertItemAsync<OrderStatus>(new OrderStatus() { Id = jobId, Status = job.Status, LastUpdated = latestStage }, new PartitionKey(jobId));

            if (upsertStatus.StatusCode != System.Net.HttpStatusCode.OK)
            {
                log.LogInformation("Upsert failed: " + upsertStatus.ToString());
            }

            return isNewStatus;
        }

        private void HandleCurrentStatus(JobResource job, ILogger log)
        {
            // Status reference: https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.management.databox.models.jobresource.status?view=azure-dotnet#microsoft-azure-management-databox-models-jobresource-status
            switch (job.Status)
            {
                case "Completed":
                case "CompletedWithErrors":
                case "CompletedWithWarnings":
                    HandleCompleted(job.Name, job.Status, job.Id);
                    break;
                case "Failed_IssueReportedAtCustomer":
                case "Failed_IssueDetectedAtAzureDC":
                    NotifyFailed(job);
                    break;
                default:
                    // if any other status then continue
                    log.LogInformation("Latest status for job: " + job.Id + " is " + job.Status);
                    break;

            }
        }

        private string GetResourceGroup(string resourceId)
        {
            // example Id would be: /subscriptions/xxxx/resourcegroups/yyyy/providers/Microsoft.DataBox/jobs/zzzz
            var splitResource = resourceId.Split("/");
            var resourceGroup = splitResource[3];

            return resourceGroup;
        }

        /// <summary>
        /// Handling errors or warnings could take time and requires retrieving more details than is provided by List operation.  Rather than blocking, we push to a queue to let something else process
        /// </summary>
        /// <param name="ordername"></param>
        /// <param name="status"></param>
        /// <param name="resourceId">Needed to get ResourceGroup of databox order to get order details like error details</param>
        private async void HandleCompleted(string ordername, string status, string resourceId)
        {

            var serviceBusFQDN = System.Environment.GetEnvironmentVariable("ServiceBusFQDN", EnvironmentVariableTarget.Process);
            var client = new ServiceBusClient(serviceBusFQDN, new ManagedIdentityCredential());
            var sender = client.CreateSender(status);

            var message = JsonConvert.SerializeObject(new DataBoxStatus { OrderName = ordername, Status = status, ResourceGroup = GetResourceGroup(resourceId) });

            await sender.SendMessageAsync(new ServiceBusMessage(message));
        }

        private void NotifyFailed(JobResource job)
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
            public string Status { get; set; }
            public DateTime? LastUpdated { get; set; }
        }
    }
}
