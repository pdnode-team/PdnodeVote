namespace PdnodeVote.Data;

public class PollOption
{
    public int Id { get; set; }
    
    public int PollId { get; set; }
    public Poll? Poll { get; set; }

    public string Text { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public int Order { get; set; }

    public List<VoteRecord> Votes { get; set; } = new();
}
