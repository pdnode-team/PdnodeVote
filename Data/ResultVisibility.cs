namespace PdnodeVote.Data;

public enum ResultVisibility
{
    AlwaysPublic = 0, // 随时公开结果
    AfterVoting = 1   // 投后可见（必须先投票或已截止才展示结果）
}
