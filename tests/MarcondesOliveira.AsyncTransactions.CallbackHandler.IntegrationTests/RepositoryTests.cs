using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using MarcondesOliveira.AsyncTransactions.CallbackHandler.Application;
using MarcondesOliveira.AsyncTransactions.CallbackHandler.Infrastructure;

namespace MarcondesOliveira.AsyncTransactions.CallbackHandler.IntegrationTests;

public sealed class RepositoryTests
{
    [Theory]
    [InlineData("PENDING", "SUCCEEDED")]
    [InlineData("PROCESSING", "SUCCEEDED")]
    [InlineData("PENDING", "REJECTED")]
    [InlineData("PROCESSING", "REJECTED")]
    [InlineData("PENDING", "FAILED")]
    [InlineData("PROCESSING", "FAILED")]
    public async Task AppliesFinalResultAndReceiptAtomically(string previous, string final)
    {
        var callback = Sample() with { Status = final };
        using var client = new StubClient();
        client.Transaction = Transaction(callback, previous);
        var repository = new DynamoDbCallbackRepository(client, "transactions-test");

        var outcome = await repository.ApplyAsync(callback, Now, CancellationToken.None);

        Assert.Equal(CallbackOutcome.Applied, outcome);
        Assert.NotNull(client.Write);
        Assert.Equal(2, client.Write.TransactItems.Count);
        var receipt = client.Write.TransactItems[0].Put;
        var update = client.Write.TransactItems[1].Update;
        Assert.Equal($"CALLBACK#{callback.EventId}", receipt.Item["PK"].S);
        Assert.Equal("attribute_not_exists(PK)", receipt.ConditionExpression);
        Assert.Equal(Now.AddHours(24).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), receipt.Item["expiresAt"].N);
        Assert.Equal(final, update.ExpressionAttributeValues[":final"].S);
        Assert.Equal(previous, update.ExpressionAttributeValues[":previous"].S);
        Assert.Contains("processorTransactionId = :processor", update.ConditionExpression, StringComparison.Ordinal);
        Assert.Contains("correlationId = :correlation", update.ConditionExpression, StringComparison.Ordinal);
        Assert.All(client.Reads, read => Assert.True(read.ConsistentRead));
    }

    [Fact]
    public async Task DuplicateDoesNotWriteAndIgnoresRetryTimestamp()
    {
        var callback = Sample();
        using var client = new StubClient { Transaction = Transaction(callback, "PROCESSING") };
        var repository = new DynamoDbCallbackRepository(client, "transactions-test");
        await repository.ApplyAsync(callback, Now, CancellationToken.None);
        client.Receipt = client.Write!.TransactItems[0].Put.Item;
        client.Write = null;

        var outcome = await repository.ApplyAsync(
            callback with { OccurredAt = Now.AddSeconds(20) }, Now.AddSeconds(30), CancellationToken.None);

        Assert.Equal(CallbackOutcome.Duplicate, outcome);
        Assert.Null(client.Write);
    }

    [Fact]
    public async Task ReusingEventIdWithChangedDataFails()
    {
        var callback = Sample();
        using var client = new StubClient { Transaction = Transaction(callback, "PROCESSING") };
        var repository = new DynamoDbCallbackRepository(client, "transactions-test");
        await repository.ApplyAsync(callback, Now, CancellationToken.None);
        client.Receipt = client.Write!.TransactItems[0].Put.Item;
        client.Write = null;

        await Assert.ThrowsAsync<CallbackConflictException>(() => repository.ApplyAsync(
            callback with { Status = "FAILED" }, Now, CancellationToken.None));
        Assert.Null(client.Write);
    }

    [Fact]
    public async Task IdenticalFinalResultOnlyAddsReceiptWithConditionCheck()
    {
        var callback = Sample();
        using var client = new StubClient { Transaction = Transaction(callback, "SUCCEEDED") };
        var repository = new DynamoDbCallbackRepository(client, "transactions-test");

        var outcome = await repository.ApplyAsync(callback, Now, CancellationToken.None);

        Assert.Equal(CallbackOutcome.Duplicate, outcome);
        Assert.NotNull(client.Write!.TransactItems[1].ConditionCheck);
        Assert.Null(client.Write.TransactItems[1].Update);
    }

    [Theory]
    [InlineData("status", "FAILED")]
    [InlineData("resultCode", "OTHER")]
    [InlineData("resultMessage", "Other result")]
    [InlineData("correlationId", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData("processorTransactionId", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    public async Task ConflictingFinalResultOrIdentityDoesNotWrite(string field, string value)
    {
        var callback = Sample();
        using var client = new StubClient { Transaction = Transaction(callback, "SUCCEEDED") };
        client.Transaction[field] = new AttributeValue { S = value };
        var repository = new DynamoDbCallbackRepository(client, "transactions-test");

        await Assert.ThrowsAsync<CallbackConflictException>(
            () => repository.ApplyAsync(callback, Now, CancellationToken.None));
        Assert.Null(client.Write);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingTransactionDoesNotCreateOrphanReceipt(bool nullItem)
    {
        using var client = new StubClient
        {
            Transaction = nullItem ? null : new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        };
        var repository = new DynamoDbCallbackRepository(client, "transactions-test");

        await Assert.ThrowsAsync<CallbackConflictException>(
            () => repository.ApplyAsync(Sample(), Now, CancellationToken.None));
        Assert.Null(client.Write);
    }

    [Fact]
    public async Task ConditionalRaceRereadsReceiptAndReturnsDuplicate()
    {
        var callback = Sample();
        using var client = new StubClient
        {
            Transaction = Transaction(callback, "PROCESSING"),
            SimulateConditionalRace = true
        };
        var repository = new DynamoDbCallbackRepository(client, "transactions-test");

        var outcome = await repository.ApplyAsync(callback, Now, CancellationToken.None);

        Assert.Equal(CallbackOutcome.Duplicate, outcome);
        Assert.Equal(1, client.WriteCount);
    }

    [Fact]
    public async Task InfrastructureFailurePropagatesForSqsRetry()
    {
        var callback = Sample();
        using var client = new StubClient
        {
            Transaction = Transaction(callback, "PROCESSING"),
            FailWrite = true
        };
        var repository = new DynamoDbCallbackRepository(client, "transactions-test");

        await Assert.ThrowsAsync<AmazonDynamoDBException>(
            () => repository.ApplyAsync(callback, Now, CancellationToken.None));
        Assert.Null(client.Receipt);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 28, 23, 30, 0, TimeSpan.Zero);

    private static TransactionProcessed Sample() => new(
        1, "TransactionProcessed", Guid.NewGuid(), Now, Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), "SUCCEEDED", new ProcessingResult("APPROVED", "Transaction processed successfully"));

    private static Dictionary<string, AttributeValue> Transaction(TransactionProcessed callback, string status) =>
        new(StringComparer.Ordinal)
        {
            ["status"] = new() { S = status },
            ["correlationId"] = new() { S = callback.CorrelationId.ToString() },
            ["processorTransactionId"] = new() { S = callback.ProcessorTransactionId.ToString() },
            ["resultCode"] = new() { S = callback.Result.Code },
            ["resultMessage"] = new() { S = callback.Result.Message }
        };

    private sealed class StubClient()
        : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public Dictionary<string, AttributeValue>? Transaction { get; set; }
        public Dictionary<string, AttributeValue>? Receipt { get; set; }
        public List<GetItemRequest> Reads { get; } = [];
        public TransactWriteItemsRequest? Write { get; set; }
        public bool SimulateConditionalRace { get; init; }
        public bool FailWrite { get; init; }
        public int WriteCount { get; private set; }

        public override Task<GetItemResponse> GetItemAsync(
            GetItemRequest request, CancellationToken cancellationToken = default)
        {
            Reads.Add(request);
            return Task.FromResult(new GetItemResponse
            {
                Item = request.Key["PK"].S.StartsWith("CALLBACK#", StringComparison.Ordinal)
                    ? Receipt : Transaction
            });
        }

        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(
            TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
        {
            Write = request;
            WriteCount++;
            if (FailWrite)
            {
                throw new AmazonDynamoDBException("Simulated service outage");
            }

            if (SimulateConditionalRace)
            {
                Receipt = request.TransactItems[0].Put.Item;
                throw new TransactionCanceledException("Simulated concurrent winner")
                {
                    CancellationReasons = [new CancellationReason { Code = "ConditionalCheckFailed" }]
                };
            }

            return Task.FromResult(new TransactWriteItemsResponse());
        }
    }
}
