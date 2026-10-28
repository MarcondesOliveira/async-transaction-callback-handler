using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using MarcondesOliveira.AsyncTransactions.CallbackHandler.Application;
using MarcondesOliveira.AsyncTransactions.CallbackHandler.Host;

namespace MarcondesOliveira.AsyncTransactions.CallbackHandler.UnitTests;

public sealed class ProcessorTests
{
    [Theory]
    [InlineData("SUCCEEDED")]
    [InlineData("REJECTED")]
    [InlineData("FAILED")]
    public async Task AcceptsTerminalStatuses(string status)
    {
        var repository = new RecordingRepository();
        var processor = new CallbackProcessor(repository, TimeProvider.System);
        var outcome = await processor.HandleAsync(Sample() with { Status = status }, CancellationToken.None);
        Assert.Equal(CallbackOutcome.Applied, outcome);
        Assert.Equal(1, repository.CallCount);
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("PROCESSING")]
    [InlineData("succeeded")]
    public async Task InvalidStatusDoesNotAccessRepository(string status)
    {
        var repository = new RecordingRepository();
        var processor = new CallbackProcessor(repository, TimeProvider.System);
        await Assert.ThrowsAsync<CallbackValidationException>(() => processor.HandleAsync(
            Sample() with { Status = status }, CancellationToken.None));
        Assert.Equal(0, repository.CallCount);
    }

    [Fact]
    public async Task InvalidVersionAndMissingResultAreRejected()
    {
        var repository = new RecordingRepository();
        var processor = new CallbackProcessor(repository, TimeProvider.System);
        await Assert.ThrowsAsync<CallbackValidationException>(() => processor.HandleAsync(
            Sample() with { EventVersion = 2 }, CancellationToken.None));
        await Assert.ThrowsAsync<CallbackValidationException>(() => processor.HandleAsync(
            Sample() with { Result = null! }, CancellationToken.None));
        await Assert.ThrowsAsync<CallbackValidationException>(() => processor.HandleAsync(
            Sample() with { EventId = Guid.Empty }, CancellationToken.None));
        Assert.Equal(0, repository.CallCount);
    }

    [Fact]
    public async Task MixedBatchOnlyRetriesFailedRecordAndLogsCorrelation()
    {
        var repository = new RecordingRepository();
        var function = new CallbackFunction(new CallbackProcessor(repository, TimeProvider.System));
        var valid = Sample();
        var context = new TestLambdaContext();
        var response = await function.FunctionHandler(new SQSEvent
        {
            Records =
            [
                new() { MessageId = "bad-record", Body = "not-json" },
                new() { MessageId = "good-record", Body = System.Text.Json.JsonSerializer.Serialize(valid) }
            ]
        }, context);

        Assert.Equal("bad-record", Assert.Single(response.BatchItemFailures).ItemIdentifier);
        Assert.Equal(1, repository.CallCount);
        var logger = Assert.IsType<TestLambdaLogger>(context.Logger);
        Assert.Contains(valid.TransactionId.ToString(), logger.Buffer.ToString(), StringComparison.Ordinal);
    }

    private static TransactionProcessed Sample() => new(
        1, "TransactionProcessed", Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), "SUCCEEDED", new ProcessingResult("APPROVED", "Approved"));

    private sealed class RecordingRepository : ICallbackRepository
    {
        public int CallCount { get; private set; }
        public Task<CallbackOutcome> ApplyAsync(
            TransactionProcessed callback, DateTimeOffset processedAt, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(CallbackOutcome.Applied);
        }
    }
}
