namespace Lodestone.Core.Community;

public sealed class CommunityTicketResult
{
    private string? ticket;

    internal CommunityTicketResult(string? ticket, int? code = null) => (this.ticket, Code) = (ticket, code);

    public int? Code { get; }

    public Uri? TakeCallback()
    {
        var value = Interlocked.Exchange(ref ticket, null);
        return value is null ? null : new UriBuilder(RisingStonesSession.Entry)
        {
            Query = RisingStonesSession.Entry.Query.TrimStart('?') + "&ticket=" + Uri.EscapeDataString(value)
        }.Uri;
    }
}
