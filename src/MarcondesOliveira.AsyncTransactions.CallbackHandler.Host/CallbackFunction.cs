using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using MarcondesOliveira.AsyncTransactions.CallbackHandler.Application;
using MarcondesOliveira.AsyncTransactions.CallbackHandler.Infrastructure;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace MarcondesOliveira.AsyncTransactions.CallbackHandler.Host;

public sealed class CallbackFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly CallbackProcessor processor;

    public CallbackFunction() : this(CreateProcessor())
    {
    }

    public CallbackFunction(CallbackProcessor processor)
    {
        this.processor = processor;
    }

    public async Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();
        foreach (var record in sqsEvent.Records)
        {
            try
            {
                var callback = JsonSerializer.Deserialize<TransactionProcessed>(record.Body, JsonOptions)
                    ?? throw new JsonException("SQS body must contain a callback event.");
                var outcome = await processor.HandleAsync(callback, CancellationToken.None);
                context.Logger.LogInformation(
                    "Callback {EventId} for transaction {TransactionId}, correlation {CorrelationId}: {Outcome}.",
                    callback.EventId, callback.TransactionId, callback.CorrelationId, outcome);
            }
            catch (Exception exception)
            {
                context.Logger.LogError(
                    "Callback SQS message {MessageId} failed: {Exception}", record.MessageId, exception);
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
            }
        }

        return new SQSBatchResponse(failures);
    }

    private static CallbackProcessor CreateProcessor()
    {
        var tableName = Environment.GetEnvironmentVariable("DynamoDb__TableName");
        if (string.IsNullOrWhiteSpace(tableName))
        {
            throw new InvalidOperationException("DynamoDb__TableName environment variable is required.");
        }

        return new CallbackProcessor(
            new DynamoDbCallbackRepository(new AmazonDynamoDBClient(), tableName), TimeProvider.System);
    }
}
