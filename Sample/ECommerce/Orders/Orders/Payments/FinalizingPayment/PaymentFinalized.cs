using Core.Events;

namespace Orders.Payments.FinalizingPayment;

public record PaymentFinalized(
    Guid OrderId,
    Guid PaymentId,
    decimal Amount,
    DateTime FinalizedAt
): IExternalEvent;
