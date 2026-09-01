using System;
namespace QuotesApi.Services;

public interface IClock
{
    DateTimeOffset UtcNow{get;}
}

public class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}