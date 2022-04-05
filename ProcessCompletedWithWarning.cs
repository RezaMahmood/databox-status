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
    public static class ProcessCompletedWithWarning
    {
        [FunctionName("ProcessCompletedWithWarning")]
        public static async Task Run([ServiceBusTrigger("completedwarnings", Connection = "ServiceBusConnection")] string myQueueItem, ILogger log)
        {
            var message = JsonConvert.DeserializeObject<DataBoxStatus>(myQueueItem);
            var subscriptionId = System.Environment.GetEnvironmentVariable("subscriptionid", EnvironmentVariableTarget.Process);
            var credential = new ManagedIdentityCredential();

            var token = credential.GetToken(new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));
            var accessToken = token.Token;
            using (var databoxClient = new DataBoxManagementClient(new TokenCredentials(accessToken)))
            {
                var job = await databoxClient.Jobs.GetAsync(message.ResourceGroup, message.OrderName, "true");

                // Trigger data ingest based on specific criteria.  For example:
                // car id: Can be parsed from OrderName
                // storage account:                 
                var databoxDiskJobDetails = (DataBoxDiskJobDetails)job.Details;
                var dataImportDetail = databoxDiskJobDetails.DataImportDetails.First();
                var storageAccountDetails = (StorageAccountDetails)dataImportDetail.AccountDetails;
                string storageAccount = storageAccountDetails.StorageAccountId; // this gives the full resource id for the storage account where databox copies to
                // error details
                var copyLogDetails = (DataBoxDiskCopyLogDetails)databoxDiskJobDetails.CopyLogDetails;
                var errorLogLocation = copyLogDetails.ErrorLogLink;
                var verboseLogLink = copyLogDetails.VerboseLogLink;

                // send error log location as part of a notification alert

            }

            log.LogInformation($"C# ServiceBus queue trigger function processed message: {myQueueItem}");
        }
    }
}

