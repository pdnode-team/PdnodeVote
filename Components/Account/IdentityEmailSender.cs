using Microsoft.AspNetCore.Identity;
using PdnodeVote.Data;
using PdnodeVote.Services;

namespace PdnodeVote.Components.Account;

public sealed class IdentityEmailSender(IEmailNotificationService emailService) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
    {
        var html = $@"
<p style=""font-size: 16px; margin-bottom: 20px;"">Hello,</p>
<p>Thank you for signing up for <strong>Pdnode Vote</strong>! Please verify your email address to complete your account setup:</p>
<div style=""margin: 28px 0;"">
    <a href=""{confirmationLink}"" style=""background-color: #10b981; color: #ffffff; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-weight: 600; font-size: 15px; display: inline-block;"">Verify Email Address</a>
</div>
<p style=""font-size: 13px; color: #94a3b8; margin-top: 24px;"">If the button above does not work, you can copy and paste the link below into your browser:</p>
<p style=""font-size: 12px; color: #38bdf8; word-break: break-all;"">{confirmationLink}</p>
<p style=""font-size: 12px; color: #64748b; margin-top: 24px;"">If you did not create an account on Pdnode Vote, you can safely ignore this email.</p>";

        return emailService.SendEmailAsync(email, "[Pdnode Vote] Verify Your Email Address", html);
    }

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink)
    {
        var html = $@"
<p style=""font-size: 16px; margin-bottom: 20px;"">Hello,</p>
<p>We received a request to reset your password for your <strong>Pdnode Vote</strong> account.</p>
<div style=""margin: 28px 0;"">
    <a href=""{resetLink}"" style=""background-color: #3b82f6; color: #ffffff; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-weight: 600; font-size: 15px; display: inline-block;"">Reset Your Password</a>
</div>
<p style=""font-size: 13px; color: #94a3b8; margin-top: 24px;"">If you did not request a password reset, you can safely ignore this email.</p>
<p style=""font-size: 12px; color: #38bdf8; word-break: break-all;"">{resetLink}</p>";

        return emailService.SendEmailAsync(email, "[Pdnode Vote] Reset Your Password", html);
    }

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode)
    {
        var html = $@"
<p style=""font-size: 16px; margin-bottom: 20px;"">Hello,</p>
<p>Your password reset verification code is:</p>
<div style=""background-color: #2a323c; border-left: 4px solid #3b82f6; padding: 14px 20px; margin: 20px 0; font-size: 24px; font-weight: bold; letter-spacing: 4px; color: #38bdf8;"">
    {resetCode}
</div>
<p style=""font-size: 13px; color: #94a3b8;"">This code will expire shortly. Please do not share it with anyone.</p>";

        return emailService.SendEmailAsync(email, "[Pdnode Vote] Password Reset Verification Code", html);
    }
}
