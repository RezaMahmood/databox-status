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

namespace databox_status
{
    public class CheckDataboxStatus
    {
        [FunctionName("CheckDataboxStatus")]
        public void Run([TimerTrigger("0 */5 * * * *")] TimerInfo myTimer, ILogger log)
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
            // Status reference: https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.management.databox.models.jobresource.status?view=azure-dotnet#microsoft-azure-management-databox-models-jobresource-status
            switch (job.Status)
            {
                case "Completed":
                    // make a call to Airflow API or add event to a queue
                    break;
                case "CompletedWithErrors":
                case "CompletedWithWarnings":
                    HandleCompletedWithIssues(job);
                    break;
                case "Failed_IssueReportedAtCustomer":
                case "Failed_IssueDetectedAtAzureDC":
                    // create an alert/email to notify someone
                    break;
                default:
                    // if any other status then continue
                    break;

            }
        }

        private void HandleCompletedWithIssues(JobResource job)
        {
            // Investigate reason for issue
            var error = job.
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
