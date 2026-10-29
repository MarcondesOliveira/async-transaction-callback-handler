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
        var logs = logger.Buffer.ToString();
        AssertIdentifiers(logs, valid);
        Assert.Contains("CallbackHandled", logs);
        Assert.Contains("good-record", logs);
        Assert.Contains("CallbackProcessingFailed", logs);
        Assert.Contains("bad-record", logs);
        Assert.Contains(nameof(System.Text.Json.JsonException), logs);
        Assert.DoesNotContain("not-json", logs);
        Assert.DoesNotContain(valid.Result.Message, logs);
    }

    [Theory]
    [InlineData(CallbackOutcome.Applied)]
    [InlineData(CallbackOutcome.Duplicate)]
    public async Task LogsAppliedAndDuplicateOutcomesWithoutPayload(CallbackOutcome outcome)
    {
        var callback = Sample();
        var (response, logs) = await InvokeAsync(callback, new OutcomeRepository(outcome));

        Assert.Empty(response.BatchItemFailures);
        Assert.Contains("CallbackHandled", logs);
        Assert.Contains("outcome={Outcome}", logs);
        Assert.Contains(outcome.ToString(), logs.Split('\n').Select(line => line.Trim()));
        Assert.Contains("test-message", logs);
        AssertIdentifiers(logs, callback);
        Assert.DoesNotContain("CallbackProcessingFailed", logs);
        Assert.DoesNotContain(callback.Result.Code, logs);
        Assert.DoesNotContain(callback.Result.Message, logs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LogsCorrelatedFailuresWithoutExceptionMessages(bool conflict)
    {
        var callback = Sample();
        Exception exception = conflict
            ? new CallbackConflictException("private-conflicting-result")
            : new InvalidOperationException("private-infrastructure-response",
                new TimeoutException("private-timeout-details"));
        var (response, logs) = await InvokeAsync(callback, new ThrowingRepository(exception));

        Assert.Equal("test-message", Assert.Single(response.BatchItemFailures).ItemIdentifier);
        Assert.Contains("CallbackProcessingFailed", logs);
        Assert.Contains(exception.GetType().Name, logs);
        Assert.Contains(exception.GetBaseException().GetType().Name, logs);
        AssertIdentifiers(logs, callback);
        Assert.DoesNotContain("CallbackHandled", logs);
        Assert.DoesNotContain("private-", logs);
        Assert.DoesNotContain(callback.Result.Message, logs);
    }

    [Fact]
    public async Task ValidationFailureKeepsIdentifiersAndDoesNotWrite()
    {
        var repository = new RecordingRepository();
        var callback = Sample() with { Status = "private-invalid-status" };
        var (response, logs) = await InvokeAsync(callback, repository);

        Assert.Single(response.BatchItemFailures);
        Assert.Equal(0, repository.CallCount);
        AssertIdentifiers(logs, callback);
        Assert.Contains(nameof(CallbackValidationException), logs);
        Assert.DoesNotContain(callback.Status, logs);
    }

    private static void AssertIdentifiers(string logs, TransactionProcessed callback)
    {
        Assert.Contains(callback.EventId.ToString(), logs);
        Assert.Contains(callback.TransactionId.ToString(), logs);
        Assert.Contains(callback.CorrelationId.ToString(), logs);
        Assert.Contains(callback.ProcessorTransactionId.ToString(), logs);
    }

    private static async Task<(SQSBatchResponse Response, string Logs)> InvokeAsync(
        TransactionProcessed callback, ICallbackRepository repository)
    {
        var logger = new TestLambdaLogger();
        var function = new CallbackFunction(new CallbackProcessor(repository, TimeProvider.System));
        var response = await function.FunctionHandler(new SQSEvent
        {
            Records = [new() { MessageId = "test-message", Body = System.Text.Json.JsonSerializer.Serialize(callback) }]
        }, new TestLambdaContext { Logger = logger });
        return (response, logger.Buffer.ToString());
    }

    private sealed class OutcomeRepository(CallbackOutcome outcome) : ICallbackRepository
    {
        public Task<CallbackOutcome> ApplyAsync(
            TransactionProcessed callback, DateTimeOffset processedAt, CancellationToken cancellationToken) =>
            Task.FromResult(outcome);
    }

    private sealed class ThrowingRepository(Exception exception) : ICallbackRepository
    {
        public Task<CallbackOutcome> ApplyAsync(
            TransactionProcessed callback, DateTimeOffset processedAt, CancellationToken cancellationToken) =>
            Task.FromException<CallbackOutcome>(exception);
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
