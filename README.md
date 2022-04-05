# databox-status

Output of this project is to check the status of a Data Box Disk order and trigger [something] when the copy process has completed.

## Tech Stack

- Azure Functions
- Data Box .NET API
- Azure Cosmos DB
- Azure Service Bus
- Azure Data Box
- Managed Identities

## Workflow

1. Azure Function with Timer trigger queries ARM for a list of all Data Box statuses for a given subscription
2. The latest status of the Data Box order is captured in Cosmos DB
3. If the status is:
        - Completed
        - CompletedWithWarnings
        - CompletedWithErrors
  then push an event to a Service Bus queue
4. Azure Function with Service Bus trigger reacts to ASB messages:
   1. Retrieve details of the Data Box order (containing addititional metadata from List operation)
   2. Parse out Storage Account details
   3. If status indicates Errors or Warnings then:
      1. Parse out CopyLogDetails and provide hook to send alert for manual intervention
