# Async Transaction Callback Handler

AWS Lambda that consumes transaction results from SQS and applies them to DynamoDB in the Async Transaction Processing portfolio project.

This service receives version 1 `TransactionProcessed` events. The HTTP callback endpoint, SNS topic, SQS queues, event-source mapping and deployment resources belong to the infrastructure repository.

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
- Logs message, event, transaction, correlation and processor identifiers on handled outcomes and on failures when the event can be deserialized.

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

There are 34 cases: 13 unit, 18 repository integration and 3 contract tests. They use controlled AWS client substitutes and require no AWS resources or credentials. Integration tests inspect the actual repository's transaction requests, including null/empty reads, conditional race recovery and infrastructure errors; they do not execute DynamoDB's condition-expression engine. Real service behavior must also be validated after deployment.

## Continuous integration

The `CI` workflow runs on pull requests targeting `main`, pushes to `main`, and
manual dispatch. It restores dependencies, verifies formatting, builds Release
with the configured .NET analyzers/warnings as errors, runs unit/integration/contract
tests and audits direct/transitive NuGet dependencies. Reported vulnerabilities
fail the job; the JSON report is printed in the job log for investigation.

The workflow follows the validated Transaction API and Request Handler CI pattern:
actions pinned to commit SHAs, read-only repository permissions and no persisted
checkout token. It requests no AWS credentials or OIDC token, publishes no
deployment package and does not modify AWS. Tests use controlled substitutes,
so a manual Bruno/Swagger test is not needed for this CI-only change. Branch
protection is separate and is not enabled by adding the workflow.

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

The baseline Lambda and callback pipeline have been deployed and validated end to end. The Stage 12 log changes require a new reviewed deployment before they appear in AWS. See [manual testing](docs/manual-testing.md) for a console event and HTTP test instructions. Bruno/Swagger calls the HTTP APIs, not this SQS Lambda directly.

## Operational logs

`CallbackHandled` records `Applied` (the result was persisted) or `Duplicate`
(the existing result was preserved). `CallbackProcessingFailed` identifies an SQS
record returned in the partial batch failure response. It includes the exception
type, root-cause type and stack trace, without exception messages, result fields
or the raw payload. Malformed JSON is traceable through `MessageId`; event IDs
are included only when deserialization succeeds and may still be invalid if
validation fails.

The native `ILambdaLogger` named parameters become top-level JSON fields when
JSON logging is enabled by Terraform. This application change alone does not
change the deployed log format. After deployment, create a transaction through
the main API with Bruno or Swagger, then inspect the callback-handler log group
for `CallbackHandled`, `Applied` and the same transaction/correlation IDs. A
repeated identical callback should log `Duplicate` without changing the final
transaction timestamps. Conflicting callbacks should log
`CallbackProcessingFailed` and leave the stored result unchanged; retry/DLQ
behavior still applies.

Cloning, building and running tests do not deploy resources. Commits and pushes are performed manually by the repository owner.
