using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace databox_status
{
    public static class ProcessCompleted
    {
        [FunctionName("ProcessCompleted")]
        public static void Run([ServiceBusTrigger("completed", Connection = "ServiceBusConnection", AutoCompleteMessages = false)] string myQueueItem,
        DateTime expiresAt,
         ILogger log)
        {
            var databoxstatus = JsonConvert.DeserializeObject<DataBoxStatus>(myQueueItem);

            log.LogInformation($"C# ServiceBus queue trigger function processed message: {myQueueItem}");
        }
    }
}
