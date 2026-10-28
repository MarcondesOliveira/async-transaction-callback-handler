# Manual Callback Handler tests

## Before deployment

Run the test suite from the repository root. There is no HTTP endpoint to start with `dotnet run`; this is an SQS Lambda library. Bruno or Swagger will exercise the callback API only after the callback pipeline exists.

The [sample Lambda event](events/callback-success.json) is a synthetic SQS invocation with the raw callback JSON in `body`. It uses illustrative identifiers; it must be adjusted to a real demonstration transaction before a successful deployed invocation.

## After deployment

1. Create a new demonstration transaction through the main API and wait for `PROCESSING`.
2. Fetch the transaction and copy its `transactionId`, `correlationId` and `processorTransactionId` into the JSON inside the sample record's `body`. Use a new UUID for `eventId` and an appropriate UTC `occurredAt`. Preserve valid JSON escaping in the outer SQS envelope.
3. Open the Callback Handler Lambda in the AWS console. Create a test event using the complete sample envelope and invoke it.
4. Inspect the returned `batchItemFailures`, logs and the main API's `GET /transactions/{transactionId}`.

For a valid success event, expect:

```json
{"batchItemFailures":[]}
```

The transaction should become `SUCCEEDED`, with the supplied processor ID and result, and the main table should contain a `CALLBACK#{eventId}` receipt. A direct console invocation updates real demonstration data.

## Duplicate and conflict checks

- Invoke the exact same event again: expect no failed items, a `Duplicate` log, and unchanged transaction `updatedAt` and `completedAt`.
- Reuse the event ID but change the result/status: expect its SQS message ID in `batchItemFailures`, a conflict log, and an unchanged transaction.
- Use a new event ID with an identical final result: expect `Duplicate`, a new receipt and no transaction timestamp change.
- Use an unknown transaction ID: expect a failed item and no orphan receipt or transaction.
- Use a mixed batch with one malformed JSON body and one valid independent event: only the malformed record's message ID should appear in failures.

A console invocation can be shown as successful by Lambda even when `batchItemFailures` contains failed records: the function returned a valid partial-batch response. Always inspect the response body and logs. Direct invocation does not trigger SQS retries or DLQ redrive; validate those through the deployed queue in the next stage.

## Through Bruno or Swagger after the callback pipeline exists

POST the inner `TransactionProcessed` JSON to the deployed callback HTTP endpoint. Do not send the outer `Records` envelope. The HTTP acknowledgement only confirms event acceptance; verify the later terminal state with the main Transaction API and inspect logs for the same event and correlation IDs.
