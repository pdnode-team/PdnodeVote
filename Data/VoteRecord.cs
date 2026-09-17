namespace PdnodeVote.Data;

public class VoteRecord
{
    public int Id { get; set; }

    public int PollId { get; set; }
    public Poll? Poll { get; set; }

    public int PollOptionId { get; set; }
    public PollOption? PollOption { get; set; }

    // 如果是登录用户，则保存 UserId
    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    // 客户端 IP 地址，无论是否登录均保存，用于免登录强校验防无痕模式刷票
    public string IpAddress { get; set; } = string.Empty;

    public DateTime VotedAt { get; set; } = DateTime.UtcNow;
}
