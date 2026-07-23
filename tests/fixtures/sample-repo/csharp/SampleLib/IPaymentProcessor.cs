namespace SampleLib;

/// <summary>Processes payments.</summary>
public interface IPaymentProcessor
{
    /// <summary>Charges the given amount and returns the amount charged.</summary>
    decimal Charge(decimal amount);
}
