using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using MarcondesOliveira.AsyncTransactions.CallbackHandler.Application;

namespace MarcondesOliveira.AsyncTransactions.CallbackHandler.Infrastructure;

public sealed class DynamoDbCallbackRepository(IAmazonDynamoDB client, string tableName)
    : ICallbackRepository
{
    public async Task<CallbackOutcome> ApplyAsync(
        TransactionProcessed callback,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        var hash = Fingerprint(callback);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var receipt = await ReadAsync($"CALLBACK#{callback.EventId}", cancellationToken);
            if (receipt is not null)
            {
                if (Value(receipt, "eventHash") != hash)
                {
                    throw new CallbackConflictException("Event ID was reused with different callback data.");
                }

                return CallbackOutcome.Duplicate;
            }

            var transactionKey = $"TRANSACTION#{callback.TransactionId}";
            var transaction = await ReadAsync(transactionKey, cancellationToken)
                ?? throw new CallbackConflictException("Callback transaction does not exist.");
            var status = Value(transaction, "status");
            var processorId = Value(transaction, "processorTransactionId");

            if (Value(transaction, "correlationId") != callback.CorrelationId.ToString() ||
                (processorId is not null && processorId != callback.ProcessorTransactionId.ToString()))
            {
                throw new CallbackConflictException("Callback correlation or processor ID does not match the transaction.");
            }

            var terminal = status is "SUCCEEDED" or "REJECTED" or "FAILED";
            if (terminal && (status != callback.Status ||
                processorId != callback.ProcessorTransactionId.ToString() ||
                Value(transaction, "resultCode") != callback.Result.Code ||
                Value(transaction, "resultMessage") != callback.Result.Message))
            {
                throw new CallbackConflictException("Callback conflicts with an existing terminal result.");
            }

            if (!terminal && status is not ("PENDING" or "PROCESSING"))
            {
                throw new CallbackConflictException("Transaction state cannot accept a callback.");
            }

            var request = BuildWrite(callback, processedAt, hash, transactionKey, status!, terminal);
            try
            {
                await client.TransactWriteItemsAsync(request, cancellationToken);
                return terminal ? CallbackOutcome.Duplicate : CallbackOutcome.Applied;
            }
            catch (TransactionCanceledException exception) when (
                exception.CancellationReasons is { Count: > 0 } reasons &&
                reasons.Any(reason => reason.Code == "ConditionalCheckFailed") &&
                reasons.All(reason => reason.Code is "None" or "ConditionalCheckFailed"))
            {
                // A competing callback or request update won. Re-read both items.
            }
        }

        throw new InvalidOperationException("Concurrent callback updates exhausted local retries; retry the SQS record.");
    }

    private async Task<Dictionary<string, AttributeValue>?> ReadAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = Key(key),
            ConsistentRead = true
        }, cancellationToken);
        return response.Item is null || response.Item.Count == 0 ? null : response.Item;
    }

    private TransactWriteItemsRequest BuildWrite(
        TransactionProcessed callback,
        DateTimeOffset processedAt,
        string hash,
        string transactionKey,
        string previousStatus,
        bool terminal)
    {
        var values = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [":previous"] = Text(previousStatus),
            [":correlation"] = Text(callback.CorrelationId.ToString()),
            [":processor"] = Text(callback.ProcessorTransactionId.ToString()),
            [":code"] = Text(callback.Result.Code),
            [":message"] = Text(callback.Result.Message)
        };
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { ["#status"] = "status" };
        const string identityCondition = "attribute_exists(PK) AND #status = :previous AND correlationId = :correlation";
        TransactWriteItem transactionWrite;

        if (terminal)
        {
            transactionWrite = new TransactWriteItem
            {
                ConditionCheck = new ConditionCheck
                {
                    TableName = tableName,
                    Key = Key(transactionKey),
                    ConditionExpression = identityCondition + " AND processorTransactionId = :processor AND resultCode = :code AND resultMessage = :message",
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = values
                }
            };
        }
        else
        {
            values[":final"] = Text(callback.Status);
            values[":processed"] = Text(Format(processedAt));
            values[":completed"] = Text(Format(callback.OccurredAt));
            transactionWrite = new TransactWriteItem
            {
                Update = new Update
                {
                    TableName = tableName,
                    Key = Key(transactionKey),
                    ConditionExpression = identityCondition + " AND (attribute_not_exists(processorTransactionId) OR processorTransactionId = :processor)",
                    UpdateExpression = "SET #status = :final, processorTransactionId = :processor, resultCode = :code, resultMessage = :message, completedAt = :completed, updatedAt = :processed",
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = values
                }
            };
        }

        var receipt = Key($"CALLBACK#{callback.EventId}");
        receipt["entityType"] = Text("CALLBACK");
        receipt["eventId"] = Text(callback.EventId.ToString());
        receipt["transactionId"] = Text(callback.TransactionId.ToString());
        receipt["eventHash"] = Text(hash);
        receipt["processedAt"] = Text(Format(processedAt));
        receipt["expiresAt"] = new AttributeValue
        {
            N = processedAt.AddHours(24).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        };

        return new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = tableName,
                        Item = receipt,
                        ConditionExpression = "attribute_not_exists(PK)"
                    }
                },
                transactionWrite
            ]
        };
    }

    // The simulator regenerates occurredAt on delivery retries. Business identity stays stable.
    private static string Fingerprint(TransactionProcessed callback) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            callback.EventVersion,
            callback.EventType,
            callback.TransactionId,
            callback.CorrelationId,
            callback.ProcessorTransactionId,
            callback.Status,
            callback.Result
        })));

    private static Dictionary<string, AttributeValue> Key(string value) =>
        new(StringComparer.Ordinal) { ["PK"] = Text(value), ["SK"] = Text(value) };

    private static string? Value(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.S : null;

    private static AttributeValue Text(string value) => new() { S = value };

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
