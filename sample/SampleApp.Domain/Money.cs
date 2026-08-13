namespace SampleApp.Domain;

public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Zero(string currency) => new(0m, currency);

    public static Money operator +(Money left, Money right)
    {
        if (left.Currency != right.Currency)
            throw new InvalidOperationException($"Cannot add {left.Currency} to {right.Currency}.");
        return left with { Amount = left.Amount + right.Amount };
    }

    public Money Times(int factor) => this with { Amount = Amount * factor };
}
