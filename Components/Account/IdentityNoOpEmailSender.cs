using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using PdnodeVote.Data;

namespace PdnodeVote.Components.Account;

// DO NOT REGISTER THIS CLASS. It is a template leftover kept only because
// RegisterConfirmation.razor type-checks against it (`EmailSender is IdentityNoOpEmailSender`) to decide
// whether it may show a confirmation link; deleting the type would break that component's build.
// The DI container binds IEmailSender<ApplicationUser> to IdentityEmailSender (Program.cs), so mails are
// never silently swallowed here — a registered no-op sender would look like "email sent" while nothing
// left the process.
internal sealed class IdentityNoOpEmailSender : IEmailSender<ApplicationUser>
{
    private readonly IEmailSender emailSender = new NoOpEmailSender();

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        emailSender.SendEmailAsync(email, "Confirm your email", $"Please confirm your account by <a href='{confirmationLink}'>clicking here</a>.");

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        emailSender.SendEmailAsync(email, "Reset your password", $"Please reset your password by <a href='{resetLink}'>clicking here</a>.");

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        emailSender.SendEmailAsync(email, "Reset your password", $"Please reset your password using the following code: {resetCode}");
}
