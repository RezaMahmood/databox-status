using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Management.DataBox;
using Azure.Identity;
using Microsoft.Rest;

namespace databox_status
{
    public class CheckDataboxStatus
    {
        [FunctionName("CheckDataboxStatus")]
        public void Run([TimerTrigger("0 */5 * * * *")] TimerInfo myTimer, ILogger log)
        {
            var credential = new DefaultAzureCredential();
            var token = credential.GetToken(new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com" }));
            var accessToken = token.Token;
            using (var client = new DataBoxManagementClient(new TokenCredentials(accessToken)))
            {
                var jobs = client.Jobs.List();
                log.LogInformation(jobs.ToString());
            }



            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");
        }
    }
}
