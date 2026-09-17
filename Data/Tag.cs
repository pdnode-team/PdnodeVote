namespace PdnodeVote.Data;

public class Tag
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int UsageCount { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<PollTag> PollTags { get; set; } = new();
}

public class PollTag
{
    public int PollId { get; set; }
    public Poll? Poll { get; set; }

    public int TagId { get; set; }
    public Tag? Tag { get; set; }
}
