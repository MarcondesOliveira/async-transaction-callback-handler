# Async Transaction Callback Handler

AWS Lambda that consumes transaction results from SQS and applies them to DynamoDB in the Async Transaction Processing portfolio project.

This service receives version 1 `TransactionProcessed` events. The HTTP callback endpoint, SNS topic, SQS queues, event-source mapping and deployment resources belong to the infrastructure repository and are introduced in the callback pipeline stage.

## Behavior

- Validates event type/version, non-empty UUIDs, UTC timestamp, terminal status and result fields before persistence.
- Accepts `SUCCEEDED`, `REJECTED` and `FAILED` results for a transaction in `PENDING` or `PROCESSING`.
- Allows callbacks that arrive before the Request Handler updates `PENDING` to `PROCESSING`.
- Checks the correlation ID and, when present, the processor transaction ID against the stored transaction.
- Atomically writes a `CALLBACK#{eventId}` receipt and updates the transaction with a conditional DynamoDB transaction.
- Treats a repeated event with the same business data as a duplicate, without another update.
- Allows another event ID for an identical terminal result by recording a receipt and condition-checking the transaction without changing its timestamps.
- Rejects a reused event ID with different business data or a conflicting terminal result.
- Returns partial SQS batch failures so successful records in the same batch are not retried.
- Logs event, transaction and correlation identifiers on success; logs message ID and exception on failure.

## Projects

```text
src/
  MarcondesOliveira.AsyncTransactions.CallbackHandler.Application/
  MarcondesOliveira.AsyncTransactions.CallbackHandler.Infrastructure/
  MarcondesOliveira.AsyncTransactions.CallbackHandler.Host/
tests/
  MarcondesOliveira.AsyncTransactions.CallbackHandler.UnitTests/
  MarcondesOliveira.AsyncTransactions.CallbackHandler.IntegrationTests/
  MarcondesOliveira.AsyncTransactions.CallbackHandler.ContractTests/
docs/
  manual-testing.md
  events/callback-success.json
```

Application owns the contract and validation. Infrastructure performs consistent reads and conditional transactional writes. Host deserializes raw SQS bodies, calls the processor and reports individual failed message IDs.

## Build and test

Use the .NET 10 SDK selected by `global.json`:

```sh
dotnet restore
dotnet build --no-restore --disable-build-servers -m:1
dotnet test --no-build --disable-build-servers -m:1
dotnet format --no-restore --verify-no-changes
```

There are 29 cases: 8 unit, 18 repository integration and 3 contract tests. They use controlled AWS client substitutes and require no AWS resources or credentials. Integration tests inspect the actual repository's transaction requests, including null/empty reads, conditional race recovery and infrastructure errors; they do not execute DynamoDB's condition-expression engine. Real service behavior must also be validated after deployment.

## Runtime configuration

| Setting | Value |
| --- | --- |
| Runtime | AWS managed `dotnet10` |
| Architecture | `arm64` |
| Package | Framework-dependent ZIP |
| Environment variable | `DynamoDb__TableName` — required main transaction table |
| Assembly | `AsyncCallback.Handler` |
| Handler | `AsyncCallback.Handler::MarcondesOliveira.AsyncTransactions.CallbackHandler.Host.CallbackFunction::FunctionHandler` |

The short assembly name keeps the handler within Lambda's 128-character limit. Infrastructure must grant table read/transactional-write permissions and log/SQS-consumption permissions, configure `ReportBatchItemFailures`, and enable SNS raw message delivery on the results subscription.

The service expects the webhook JSON directly in each SQS `body`. An SNS notification envelope is not supported. No Swagger or HTTP listener is exposed by this Lambda.

## Persistence and duplicate handling

Transaction keys remain `TRANSACTION#{transactionId}` for both PK and SK. The result updates `status`, `processorTransactionId`, `resultCode`, `resultMessage`, `completedAt` (the event's UTC timestamp) and `updatedAt` (handler processing time).

The receipt uses `CALLBACK#{eventId}` for both keys and stores `entityType`, event/transaction IDs, a SHA-256 `eventHash`, processing time and `expiresAt`. Its default TTL is 24 hours. The transaction itself receives no TTL from this handler and remains available as the business record.

The receipt and transaction change are one atomic write: a failed transaction cannot leave a receipt that would incorrectly suppress a retry. Conditional writes protect both items against races. The repository re-reads after a conditional cancellation, up to three local attempts; unresolved contention is returned as an SQS failure.

The hash covers version/type, transaction, correlation, processor, final status and result. It excludes `occurredAt` because the current simulator regenerates that timestamp when retrying delivery. The first applied completion time is preserved. Changed business data under the same event ID is a conflict.

TTL deletion is asynchronous. After a receipt is deleted, the retained transaction's terminal status and result still prevent a different final result or another state update. A new receipt may be written for an identical final result. This is idempotent processing under at-least-once delivery, not an exactly-once transport guarantee.

## Error and retry policy

Malformed JSON, unknown fields, invalid events, missing transactions, identity mismatches, final-result conflicts and infrastructure errors all fail only their own SQS record. The source queue must eventually redrive exhausted failures to its DLQ for investigation. Invalid/conflicting messages are deliberately not silently acknowledged or discarded.

This handler does not create missing transactions, authenticate the external sender, publish SNS messages or manage AWS resources. Webhook security remains the agreed HTTPS demonstration scope.

## Manual tests and deployment status

The code is prepared for local review and testing; this Lambda has not yet been deployed. See [manual testing](docs/manual-testing.md) for a console event and the expected results after deployment. At this stage, Bruno/Swagger cannot call this handler directly. End-to-end HTTP testing becomes possible after the callback endpoint, SNS and SQS pipeline are deployed.

Cloning, building and running tests do not deploy resources. Commits and pushes are performed manually by the repository owner.
