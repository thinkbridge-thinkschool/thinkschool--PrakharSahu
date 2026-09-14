using System;
namespace QuotesApi.SharedKernel;

public interface IClock
{
    DateTimeOffset UtcNow{get;}
}

public class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}