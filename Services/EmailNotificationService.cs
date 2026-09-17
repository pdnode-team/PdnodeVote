using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PdnodeVote.Data;

namespace PdnodeVote.Services;

public class EmailNotificationService : IEmailNotificationService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailNotificationService> _logger;

    public EmailNotificationService(IConfiguration configuration, ILogger<EmailNotificationService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string WrapHtml(string title, string contentHtml)
    {
        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <title>{title}</title>
    <style>
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #12161a; color: #e2e8f0; margin: 0; padding: 24px; }}
        .card {{ max-width: 600px; margin: 0 auto; background-color: #1d232a; border: 1px solid #2a323c; border-radius: 12px; padding: 32px; box-shadow: 0 4px 16px rgba(0,0,0,0.4); }}
        .brand {{ font-size: 22px; font-weight: bold; color: #10b981; margin-bottom: 24px; display: flex; align-items: center; }}
        .title {{ font-size: 18px; font-weight: 600; color: #f8fafc; margin-bottom: 16px; border-bottom: 1px solid #2a323c; padding-bottom: 10px; }}
        .content {{ line-height: 1.6; font-size: 15px; color: #cbd5e1; }}
        .reason-box {{ background-color: #2a323c; border-left: 4px solid #f59e0b; padding: 14px 18px; margin: 18px 0; border-radius: 6px; color: #fef3c7; }}
        .danger-box {{ background-color: #2a323c; border-left: 4px solid #ef4444; padding: 14px 18px; margin: 18px 0; border-radius: 6px; color: #fecaca; }}
        .success-box {{ background-color: #2a323c; border-left: 4px solid #10b981; padding: 14px 18px; margin: 18px 0; border-radius: 6px; color: #a7f3d0; }}
        .footer {{ margin-top: 32px; border-top: 1px solid #2a323c; padding-top: 16px; font-size: 12px; color: #64748b; text-align: center; }}
    </style>
</head>
<body>
    <div class=""card"">
        <div class=""brand"">Pdnode Vote</div>
        <div class=""title"">{title}</div>
        <div class=""content"">
            {contentHtml}
        </div>
        <div class=""footer"">
            This is an automated notification from Pdnode Vote. Please do not reply directly to this email.
        </div>
    </div>
</body>
</html>";
    }

    public async Task SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        if (string.IsNullOrWhiteSpace(toEmail)) return;

        var host = _configuration["SmtpSettings:Host"];
        var portStr = _configuration["SmtpSettings:Port"];
        var username = _configuration["SmtpSettings:Username"];
        var password = _configuration["SmtpSettings:Password"];
        var senderEmail = _configuration["SmtpSettings:SenderEmail"] ?? "noreply@pdnodevote.com";
        var senderName = _configuration["SmtpSettings:SenderName"] ?? "Pdnode Vote";

        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogInformation("[Email Dispatched (Console Fallback)] To: {ToEmail} | Subject: {Subject}\nBody Content:\n{Body}", 
                toEmail, subject, htmlBody);
            return;
        }

        try
        {
            int port = int.TryParse(portStr, out var p) ? p : 587;
            bool enableSsl = bool.TryParse(_configuration["SmtpSettings:EnableSsl"], out var ssl) ? ssl : true;

            using var message = new MailMessage
            {
                From = new MailAddress(senderEmail, senderName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            message.To.Add(toEmail);

            using var client = new SmtpClient(host, port)
            {
                EnableSsl = enableSsl
            };

            if (!string.IsNullOrWhiteSpace(username))
            {
                client.Credentials = new NetworkCredential(username, password);
            }

            await client.SendMailAsync(message);
            _logger.LogInformation("Successfully sent email to {ToEmail} with subject: {Subject}", toEmail, subject);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SMTP email to {ToEmail}. Fallback logged. Subject: {Subject}", toEmail, subject);
        }
    }

    public async Task SendBulkEmailAsync(IEnumerable<string> toEmails, string subject, string htmlBody)
    {
        var distinctEmails = toEmails.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct().ToList();
        foreach (var email in distinctEmails)
        {
            await SendEmailAsync(email, subject, htmlBody);
        }
    }

    public async Task NotifyPollApprovedAsync(Poll poll, string authorEmail)
    {
        var title = "Your poll has been approved!";
        var body = $@"
<div class=""success-box"">
    <strong>Congratulations!</strong> Your poll <strong>""{System.Net.WebUtility.HtmlEncode(poll.Title)}""</strong> has been approved by the moderation team.
</div>
<p>It is now live on Pdnode Vote for the community to discover and vote.</p>
<p>Thank you for contributing quality discussions to our community!</p>";

        await SendEmailAsync(authorEmail, $"[Pdnode Vote] Poll Approved: {poll.Title}", WrapHtml(title, body));
    }

    public async Task NotifyPollReturnedForRevisionAsync(Poll poll, string authorEmail, string reason)
    {
        var title = "Action Required: Poll returned for revision";
        var body = $@"
<p>Your poll <strong>""{System.Net.WebUtility.HtmlEncode(poll.Title)}""</strong> requires revisions before it can be published.</p>
<div class=""reason-box"">
    <strong>Moderator Feedback:</strong><br/>
    {System.Net.WebUtility.HtmlEncode(reason)}
</div>
<p>Please log in to your account and navigate to <strong>My Polls</strong> or open the poll link to edit your poll and resubmit it for review.</p>";

        await SendEmailAsync(authorEmail, $"[Pdnode Vote] Action Required: Poll Returned for Revision - {poll.Title}", WrapHtml(title, body));
    }

    public async Task NotifyPollRemovedAsync(Poll poll, string authorEmail, string reason)
    {
        var title = "Notice: Your poll has been removed";
        var body = $@"
<div class=""danger-box"">
    Your poll <strong>""{System.Net.WebUtility.HtmlEncode(poll.Title)}""</strong> has been removed by the moderation team.
</div>
<div class=""reason-box"">
    <strong>Reason for Removal:</strong><br/>
    {System.Net.WebUtility.HtmlEncode(reason)}
</div>
<p>In accordance with community rules, this poll is no longer publicly accessible. You may still view the poll details from your My Polls page.</p>";

        await SendEmailAsync(authorEmail, $"[Pdnode Vote] Poll Removed: {poll.Title}", WrapHtml(title, body));
    }

    public async Task NotifyPollArchivedAsync(Poll poll, string authorEmail, string? reason)
    {
        var title = "Notice: Your poll has been archived";
        var reasonHtml = string.IsNullOrWhiteSpace(reason) ? "" : $@"
<div class=""reason-box"">
    <strong>Moderator Note:</strong><br/>
    {System.Net.WebUtility.HtmlEncode(reason)}
</div>";

        var body = $@"
<p>Your poll <strong>""{System.Net.WebUtility.HtmlEncode(poll.Title)}""</strong> has been archived.</p>
{reasonHtml}
<p>Voting is now closed. The final results remain preserved for reference.</p>";

        await SendEmailAsync(authorEmail, $"[Pdnode Vote] Poll Archived: {poll.Title}", WrapHtml(title, body));
    }

    public async Task NotifyPollPinnedAsync(Poll poll, string authorEmail, bool isPinned)
    {
        var action = isPinned ? "pinned to the top of the community" : "unpinned";
        var title = isPinned ? "Your poll has been pinned!" : "Your poll has been unpinned";
        var body = $@"
<p>Your poll <strong>""{System.Net.WebUtility.HtmlEncode(poll.Title)}""</strong> has been {action} by community administrators.</p>";

        await SendEmailAsync(authorEmail, $"[Pdnode Vote] Poll {action}: {poll.Title}", WrapHtml(title, body));
    }

    public async Task NotifyAccountBannedAsync(string email, bool isPermanent, DateTime? bannedUntil, string reason)
    {
        var title = isPermanent ? "Account Suspended Permanently" : "Account Suspended Temporarily";
        var durationText = isPermanent 
            ? "Permanent" 
            : $"Until {bannedUntil?.ToString("yyyy-MM-dd HH:mm UTC")}";

        var body = $@"
<div class=""danger-box"">
    <strong>Your Pdnode Vote account has been suspended.</strong>
</div>
<p><strong>Duration:</strong> {durationText}</p>
<div class=""reason-box"">
    <strong>Reason:</strong><br/>
    {System.Net.WebUtility.HtmlEncode(reason)}
</div>
<p>During the suspension, your account cannot create polls or submit votes. If you believe this was in error, please contact community support.</p>";

        await SendEmailAsync(email, $"[Pdnode Vote] Notice of Account Suspension", WrapHtml(title, body));
    }

    public async Task NotifyAccountUnbannedAsync(string email)
    {
        var title = "Account Suspension Lifted";
        var body = $@"
<div class=""success-box"">
    <strong>Your account suspension has been lifted.</strong>
</div>
<p>Full access to creating polls and voting has been restored to your Pdnode Vote account. Welcome back!</p>";

        await SendEmailAsync(email, $"[Pdnode Vote] Account Suspension Lifted", WrapHtml(title, body));
    }

    public async Task NotifyPasswordResetAsync(string email, string newPassword)
    {
        var title = "Your Password Has Been Reset";
        var body = $@"
<p>An administrator has reset your password for Pdnode Vote.</p>
<div class=""reason-box"">
    <strong>Your Temporary Password:</strong><br/>
    <code style=""font-size: 16px; color: #10b981; font-weight: bold;"">{System.Net.WebUtility.HtmlEncode(newPassword)}</code>
</div>
<p>Please log in with this temporary password and immediately change it to a secure personal password in your Account Settings.</p>";

        await SendEmailAsync(email, $"[Pdnode Vote] Temporary Password Generated", WrapHtml(title, body));
    }
}
