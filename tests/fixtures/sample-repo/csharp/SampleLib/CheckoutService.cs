namespace SampleLib;

/// <summary>Coordinates checkout for an order.</summary>
public class CheckoutService
{
    private readonly IPaymentProcessor _processor;

    /// <summary>Creates a checkout service backed by the given payment processor.</summary>
    public CheckoutService(IPaymentProcessor processor)
    {
        _processor = processor;
    }

    /// <summary>Completes checkout by charging the given amount.</summary>
    public decimal Complete(decimal amount)
    {
        return _processor.Charge(amount);
    }
}
