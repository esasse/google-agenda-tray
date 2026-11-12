namespace AgendaTray.Agenda;

/// <summary>One appointment, merged from any account and calendar. Same fields the
/// original helper put in its JSON, so the two read the same way.</summary>
internal sealed class AgendaEvent
{
    public string Id { get; set; } = "";
    public string Account { get; set; } = "";
    public string AccountLabel { get; set; } = "";
    public string CalendarId { get; set; } = "";
    public string Calendar { get; set; } = "";
    public bool Primary { get; set; }
    public string Color { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public bool AllDay { get; set; }
    public bool Busy { get; set; }
    public bool Recurring { get; set; }
    public string Location { get; set; } = "";
    public string JoinUrl { get; set; } = "";
    public string JoinKind { get; set; } = "";
    public string HtmlLink { get; set; } = "";
    /// <summary>The Google identity the links open under: the owner of a shared
    /// calendar, otherwise the account that fetched the event.</summary>
    public string OpenAs { get; set; } = "";
    public string MyStatus { get; set; } = "";
    public string Organizer { get; set; } = "";
    public bool OrganizerSelf { get; set; }
    public int AttendeeCount { get; set; }

    public string Key => Account + "|" + Id;
}

internal sealed class AgendaResult
{
    public bool Ok { get; set; }
    public string Date { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    /// <summary>Served from the day's cache because Google could not be reached.</summary>
    public bool Stale { get; set; }
    public bool NeedsSetup { get; set; }
    public bool NeedsLogin { get; set; }
    public List<string> Reauth { get; set; } = [];
    /// <summary>Accounts that brought back less than their whole day (a token, the
    /// calendar list or one calendar failed): the day shown is missing their part.</summary>
    public List<string> FailedAccounts { get; set; } = [];
    public List<string> Accounts { get; set; } = [];
    public List<AgendaEvent> Events { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}
