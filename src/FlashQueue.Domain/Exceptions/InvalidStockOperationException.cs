namespace FlashQueue.Domain.Exceptions;

public sealed class InvalidStockOperationException : DomainException
{
    public InvalidStockOperationException(string message) : base(message)
    {
    }
}
