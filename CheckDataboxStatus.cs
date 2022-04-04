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


namespace databox_status
{
    public class CheckDataboxStatus
    {
        /// <summary>
        /// Runs every 2 hours as per cron expression
        /// </summary>
        /// <param name="myTimer"></param>
        /// <param name="log"></param>
        [FunctionName("CheckDataboxStatus")]
        public void Run([TimerTrigger("0 */2 * * * *")] TimerInfo myTimer, ILogger log)
        {
            var subscriptionId = "";
            var credential = new DefaultAzureCredential();

            var token = credential.GetToken(new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));
            var accessToken = token.Token;
            using (var client = new DataBoxManagementClient(new TokenCredentials(accessToken)))
            {
                client.SubscriptionId = subscriptionId;
                var jobs = client.Jobs.List().ToList<JobResource>(); // Lists all the jobs in the subscription


                foreach (var job in jobs)
                {
                    // JobResource reference: https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.management.databox.models.jobresource?view=azure-dotnet
                    var jobName = job.Name;
                    //var carDetails = ParseCarDetails(jobName);

                    // Log order details into a database if you wish to create a dashboard
                    // Log latest order status into database

                    // Handle current status
                    HandleCurrentStatus(job);
                    log.LogInformation(JsonConvert.SerializeObject(jobs).ToString());
                }
            }

            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");
        }

        private void HandleCurrentStatus(JobResource job)
        {
            //TODO: Need to put a check in here as to whether this status has already been handled - need to check database to see if latest status matches this one


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
            var client = new ServiceBusClient(serviceBusFQDN, new DefaultAzureCredential());
            var sender = client.CreateSender(status);

            var message = JsonConvert.SerializeObject(new DataBoxStatus { OrderName = ordername, Status = status, ResourceGroup = GetResourceGroup(resourceId) });

            await sender.SendMessageAsync(new ServiceBusMessage(message));
        }

        private void NotifyFailed(JobResource job)
        {
            // this could make a call to a Logic App to send email notifications with O365 connector
        }

        private CarDetails ParseCarDetails(string jobName)
        {
            // assumes naming convention of YYYYMMDD_CountryCode_CarId_OrderNumber
            var jobstring = jobName.Split('_');
            var carDetails = new CarDetails()
            {
                CountryCode = jobstring[1],
                CarId = jobstring[2],
                OrderNumber = Convert.ToInt32(jobstring[3])
            };

            return carDetails;
        }

        private class CarDetails
        {
            public string CarId { get; set; }
            public string CountryCode { get; set; }
            public int OrderNumber { get; set; }
        }
    }
}
