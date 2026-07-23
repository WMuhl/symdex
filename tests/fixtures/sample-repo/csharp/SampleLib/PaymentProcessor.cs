namespace SampleLib;

/// <summary>Default payment processor implementation.</summary>
public class PaymentProcessor : IPaymentProcessor
{
    /// <summary>Charges the given amount and returns the amount charged.</summary>
    public decimal Charge(decimal amount)
    {
        return amount;
    }
}
