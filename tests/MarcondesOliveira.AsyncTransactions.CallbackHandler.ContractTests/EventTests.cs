using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using MarcondesOliveira.AsyncTransactions.CallbackHandler.Application;
using MarcondesOliveira.AsyncTransactions.CallbackHandler.Host;

namespace MarcondesOliveira.AsyncTransactions.CallbackHandler.ContractTests;

public sealed class EventTests
{
    private const string Body = """
        {"eventVersion":1,"eventType":"TransactionProcessed",
        "eventId":"5fd68392-40e3-4273-b6cc-b5546342ce28","occurredAt":"2026-10-28T18:45:35.481Z",
        "transactionId":"06f26a61-3ae3-4f80-a4e9-061a3a73ad6d",
        "correlationId":"f452ea72-e88d-48e9-875f-3f5e7f2736e6",
        "processorTransactionId":"dbd539d5-dfb1-4ed6-b8c8-1ad6f2d096ac",
        "status":"SUCCEEDED","result":{"code":"APPROVED","message":"Transaction processed successfully"}}
        """;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [Fact]
    public void ContractExampleDeserializes()
    {
        var callback = JsonSerializer.Deserialize<TransactionProcessed>(Body, Options);
        Assert.NotNull(callback);
        Assert.Equal("SUCCEEDED", callback.Status);
        Assert.Equal("APPROVED", callback.Result.Code);
        Assert.Equal(TimeSpan.Zero, callback.OccurredAt.Offset);
    }

    [Fact]
    public void UnknownFieldIsRejected()
    {
        var changed = Body.Replace("\"eventVersion\":1", "\"unexpected\":true,\"eventVersion\":1", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TransactionProcessed>(changed, Options));
    }

    [Fact]
    public async Task SnsEnvelopeIsNotAcceptedAsRawEvent()
    {
        var function = new CallbackFunction(new CallbackProcessor(new NoWriteRepository(), TimeProvider.System));
        var response = await function.FunctionHandler(new SQSEvent
        {
            Records = [new() { MessageId = "sns-envelope", Body = JsonSerializer.Serialize(new { Type = "Notification", Message = Body }) }]
        }, new TestLambdaContext());
        Assert.Equal("sns-envelope", Assert.Single(response.BatchItemFailures).ItemIdentifier);
    }

    private sealed class NoWriteRepository : ICallbackRepository
    {
        public Task<CallbackOutcome> ApplyAsync(
            TransactionProcessed callback, DateTimeOffset processedAt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Invalid contracts must never reach persistence.");
    }
}
