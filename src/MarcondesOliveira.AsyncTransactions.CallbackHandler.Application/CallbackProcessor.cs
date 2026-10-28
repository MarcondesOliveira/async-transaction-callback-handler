namespace MarcondesOliveira.AsyncTransactions.CallbackHandler.Application;

public sealed class CallbackProcessor(ICallbackRepository repository, TimeProvider timeProvider)
{
    public Task<CallbackOutcome> HandleAsync(
        TransactionProcessed callback,
        CancellationToken cancellationToken)
    {
        Validate(callback);
        return repository.ApplyAsync(callback, timeProvider.GetUtcNow(), cancellationToken);
    }

    private static void Validate(TransactionProcessed callback)
    {
        if (callback.EventVersion != 1 || callback.EventType != "TransactionProcessed")
        {
            throw new CallbackValidationException("Expected TransactionProcessed event version 1.");
        }

        if (callback.EventId == Guid.Empty || callback.TransactionId == Guid.Empty ||
            callback.CorrelationId == Guid.Empty || callback.ProcessorTransactionId == Guid.Empty)
        {
            throw new CallbackValidationException("All event identifiers must be non-empty UUIDs.");
        }

        if (callback.OccurredAt == default || callback.OccurredAt.Offset != TimeSpan.Zero)
        {
            throw new CallbackValidationException("occurredAt must be a non-default UTC timestamp.");
        }

        if (callback.Status is not ("SUCCEEDED" or "REJECTED" or "FAILED"))
        {
            throw new CallbackValidationException("Callback status must be terminal.");
        }

        if (callback.Result is null || string.IsNullOrWhiteSpace(callback.Result.Code) ||
            string.IsNullOrWhiteSpace(callback.Result.Message))
        {
            throw new CallbackValidationException("Result code and message are required.");
        }
    }
}
