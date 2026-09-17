using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PdnodeVote.Client.Models;
using PdnodeVote.Data;
using PdnodeVote.Services;

namespace PdnodeVote.Endpoints;

public static class PollEndpoints
{
    public static void MapPollEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/polls");

        // Minimal APIs get no antiforgery validation from UseAntiforgery() alone (verified with a
        // probe: an untokened JSON POST was still accepted), so state-changing endpoints opt in
        // explicitly. Safe methods pass straight through the filter.
        group.AddEndpointFilter<AntiforgeryEndpointFilter>();

        // GET /api/polls
        group.MapGet("/", async (
            PollService pollService,
            [FromQuery] string? search,
            [FromQuery] string? status,
            [FromQuery] string? sortBy,
            [FromQuery] int? categoryId,
            [FromQuery] string? tag) =>
        {
            var polls = await pollService.GetPollsAsync(search, status, sortBy, categoryId, tag);
            return Results.Ok(polls);
        });

        // GET /api/polls/feed (paginated with load more & author filter)
        group.MapGet("/feed", async (
            PollService pollService,
            HttpContext httpContext,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 15,
            [FromQuery] string? search = null,
            [FromQuery] string? status = "all",
            [FromQuery] string? sortBy = "latest",
            [FromQuery] int? categoryId = null,
            [FromQuery] string? tag = null,
            [FromQuery] string? author = null,
            [FromQuery] bool onlySubscribed = false) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var res = await pollService.GetPollFeedAsync(page, pageSize, search, status, sortBy, categoryId, tag, author, onlySubscribed, userId);
            return Results.Ok(res);
        });

        // GET /api/polls/{id}
        group.MapGet("/{id:int}", async (
            int id,
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var clientIp = GetClientIp(httpContext);
            var poll = await pollService.GetPollDetailAsync(id, userId, clientIp);
            if (poll == null) return Results.NotFound();
            return Results.Ok(poll);
        });

        // GET /api/polls/{id}/qrcode
        group.MapGet("/{id:int}/qrcode", async (
            int id,
            PollService pollService,
            IConfiguration configuration,
            HttpContext context) =>
        {
            // A QR code for content that does not exist (or that the caller may not see) is useless and
            // was previously minted for any id, so validate first.
            var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var poll = await pollService.GetPollDetailAsync(id, userId, GetClientIp(context));
            if (poll == null) return Results.NotFound();

            var pollUrl = BuildPublicPollUrl(configuration, context, id);
            var qr = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(pollUrl, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Medium);
            var svg = qr.ToSvgString(4);
            return Results.Content(svg, "image/svg+xml");
        });

        // POST /api/polls
        group.MapPost("/", async (
            CreatePollRequest request,
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var visibility = request.ResultVisibility == Client.Models.ResultVisibility.AfterVoting
                ? PdnodeVote.Data.ResultVisibility.AfterVoting
                : PdnodeVote.Data.ResultVisibility.AlwaysPublic;

            var (success, message, pollId) = await pollService.CreatePollAsync(
                request.Title,
                request.Description,
                userId,
                request.RequireLogin,
                request.IsMultipleChoice,
                request.MaxChoices,
                visibility,
                request.ExpiresAt,
                request.Options,
                request.CategoryId,
                request.Tags,
                request.OptionItems
            );

            if (!success) return Results.BadRequest(new ServiceResult { Success = false, Message = message });
            return Results.Ok(new ServiceResult { Success = true, Message = message, PollId = pollId });
        }).RequireAuthorization();

        // POST /api/polls/{id}/vote
        group.MapPost("/{id:int}/vote", async (
            int id,
            VoteRequest request,
            PollService pollService,
            IConfiguration config,
            IHttpClientFactory httpClientFactory,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var clientIp = GetClientIp(httpContext);

            // Turnstile bot check for unauthenticated guests. Enabled by configuring Turnstile:SecretKey.
            var turnstileSecret = config["Turnstile:SecretKey"];
            if (string.IsNullOrEmpty(userId) && !string.IsNullOrEmpty(turnstileSecret))
            {
                if (string.IsNullOrWhiteSpace(request.TurnstileToken))
                {
                    return Results.BadRequest(new ServiceResult { Success = false, Message = "Security check required for guest votes." });
                }

                bool verified;
                try
                {
                    var client = httpClientFactory.CreateClient();
                    var verifyResponse = await client.PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify", new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["secret"] = turnstileSecret,
                        ["response"] = request.TurnstileToken,
                        ["remoteip"] = clientIp
                    }));

                    if (!verifyResponse.IsSuccessStatusCode)
                    {
                        // Previously a non-success response simply fell through and the guest vote was
                        // accepted, so any way of making siteverify fail (outage, 5xx, blocked egress)
                        // disabled the check entirely. Fail closed instead.
                        return Results.BadRequest(new ServiceResult { Success = false, Message = "Verification challenge could not be validated. Please try again." });
                    }

                    var json = await verifyResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                    verified = json.TryGetProperty("success", out var successProp)
                               && successProp.ValueKind == System.Text.Json.JsonValueKind.True;
                }
                catch
                {
                    // Same reasoning as above: an unreachable verification service must not silently
                    // turn into "no verification required".
                    return Results.BadRequest(new ServiceResult { Success = false, Message = "Verification challenge could not be validated. Please try again." });
                }

                if (!verified)
                {
                    return Results.BadRequest(new ServiceResult { Success = false, Message = "Verification challenge failed. Please try again." });
                }
            }

            var (success, message) = await pollService.CastVoteAsync(id, userId, clientIp, request.SelectedOptionIds);
            if (!success) return Results.BadRequest(new ServiceResult { Success = false, Message = message });
            return Results.Ok(new ServiceResult { Success = true, Message = message, PollId = id });
        });

        // GET /api/polls/my
        group.MapGet("/my", async (
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var polls = await pollService.GetUserPollsAsync(userId);
            return Results.Ok(polls);
        }).RequireAuthorization();

        // DELETE /api/polls/{id}
        group.MapDelete("/{id:int}", async (
            int id,
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var (success, message) = await pollService.DeletePollAsync(id, userId);
            if (!success) return Results.BadRequest(new ServiceResult { Success = false, Message = message });
            return Results.Ok(new ServiceResult { Success = true, Message = message, PollId = id });
        }).RequireAuthorization();

        // PUT /api/polls/{id} (Safe in-place update)
        group.MapPut("/{id:int}", async (
            int id,
            UpdatePollRequest request,
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var res = await pollService.UpdatePollAsync(id, userId, request);
            if (!res.Success) return Results.BadRequest(res);
            return Results.Ok(res);
        }).RequireAuthorization();

        // PUT /api/polls/{id}/resubmit (Revision resubmit)
        group.MapPut("/{id:int}/resubmit", async (
            int id,
            ResubmitPollRequest request,
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var (success, message) = await pollService.UpdateAndResubmitPollAsync(id, userId, request.Title, request.Description, request.Options, request.CategoryId, request.Tags);
            if (!success) return Results.BadRequest(new ServiceResult { Success = false, Message = message });
            return Results.Ok(new ServiceResult { Success = true, Message = message, PollId = id });
        }).RequireAuthorization();

        // POST /api/polls/{id}/withdraw
        group.MapPost("/{id:int}/withdraw", async (
            int id,
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var res = await pollService.WithdrawToDraftAsync(id, userId);
            if (!res.Success) return Results.BadRequest(res);
            return Results.Ok(res);
        }).RequireAuthorization();

        // GET /api/polls/{id}/comments
        group.MapGet("/{id:int}/comments", async (
            int id,
            ICommentService commentService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var comments = await commentService.GetPollCommentsAsync(id, userId);
            return Results.Ok(comments);
        });

        // POST /api/polls/{id}/comments
        group.MapPost("/{id:int}/comments", async (
            int id,
            CreateCommentRequest request,
            ICommentService commentService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var result = await commentService.AddCommentAsync(id, userId, request.Content, request.ParentCommentId);
            if (!result.Success) return Results.BadRequest(result);
            return Results.Ok(result);
        }).RequireAuthorization();

        // GET /api/polls/{id}/export-csv
        // Requires authentication and respects ResultVisibility: the export used to be anonymous
        // and dumped the exact distribution of an "AfterVoting" poll whether or not the caller had
        // voted, leaking the very result that setting is meant to withhold.
        group.MapGet("/{id:int}/export-csv", async (
            int id,
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var clientIp = GetClientIp(httpContext);
            var poll = await pollService.GetPollDetailAsync(id, userId, clientIp);
            if (poll == null) return Results.NotFound();

            if (!poll.ResultsVisible)
            {
                return Results.BadRequest(new ServiceResult
                {
                    Success = false,
                    Message = "Results for this poll are only available after you vote."
                });
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Poll ID,Title,Status,Created At (UTC),Expires At (UTC),Total Participants,Total Votes");
            sb.AppendLine($"\"{poll.Id}\",\"{poll.Title.Replace("\"", "\"\"")}\",\"{poll.Status}\",\"{poll.CreatedAt:yyyy-MM-dd HH:mm:ss}\",\"{(poll.ExpiresAt.HasValue ? poll.ExpiresAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : "None")}\",\"{poll.TotalParticipants}\",\"{poll.TotalVotesCount}\"");
            sb.AppendLine();
            sb.AppendLine("Option Order,Option Text,Vote Count,Vote Percentage,Has Image");
            foreach (var opt in poll.Options)
            {
                sb.AppendLine($"\"{opt.Order}\",\"{opt.Text.Replace("\"", "\"\"")}\",\"{opt.VoteCount}\",\"{opt.Percentage:F1}%\",\"{(string.IsNullOrEmpty(opt.ImageUrl) ? "No" : "Yes")}\"");
            }

            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return Results.File(bytes, "text/csv", $"poll-{id}-results.csv");
        }).RequireAuthorization();

        // POST /api/comments/{id}/upvote
        routes.MapPost("/api/comments/{id:int}/upvote", async (
            int id,
            ICommentService commentService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var res = await commentService.UpvoteCommentAsync(id, userId);
            if (!res.Success) return Results.BadRequest(res);
            return Results.Ok(res);
        }).RequireAuthorization();

        // POST /api/comments/{id}/pin
        routes.MapPost("/api/comments/{id:int}/pin", async (
            int id,
            ICommentService commentService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var res = await commentService.TogglePinCommentAsync(id, userId);
            if (!res.Success) return Results.BadRequest(res);
            return Results.Ok(res);
        }).RequireAuthorization();

        // POST /api/upload/image
        routes.MapPost("/api/upload/image", async (
            IFormFile file,
            IWebHostEnvironment env,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "No file provided." });

            if (file.Length > 5 * 1024 * 1024)
                return Results.BadRequest(new { error = "File size exceeds 5MB limit." });

            var allowedExts = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowedExts.Contains(ext))
                return Results.BadRequest(new { error = "Invalid image file format. Supported: JPG, PNG, WEBP, GIF." });

            var uploadsFolder = Path.Combine(env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "uploads");
            if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

            var safeName = $"opt_{Guid.NewGuid():N}{ext}";
            var filePath = Path.Combine(uploadsFolder, safeName);
            await using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            return Results.Ok(new { url = $"/uploads/{safeName}" });
        }).RequireAuthorization();
        // NOTE: no antiforgery validation here, and `.DisableAntiforgery()` was removed because it
        // was a no-op: verified on .NET 10 that app.UseAntiforgery() does NOT guard minimal APIs
        // (JSON or form) — a request without any token is still accepted — and there is no
        // RequireAntiforgery() extension for RouteHandlerBuilder. See TODO-BUGS.md S10.
        // What actually blocks a cross-site POST today is the auth cookie's SameSite=Lax: a
        // cross-site form post does not carry .PdnodeVote.Auth, so the handler returns Unauthorized.

        // GET /api/announcement
        routes.MapGet("/api/announcement", async (IDbContextFactory<ApplicationDbContext> dbFactory) =>
        {
            await using var context = await dbFactory.CreateDbContextAsync();
            var item = await context.SystemAnnouncements.AsNoTracking().FirstOrDefaultAsync(a => a.IsActive);
            if (item == null) return Results.Ok<SystemAnnouncementDto?>(null);
            return Results.Ok<SystemAnnouncementDto?>(new SystemAnnouncementDto
            {
                Id = item.Id,
                Message = item.Message,
                TargetUrl = item.TargetUrl,
                IsActive = item.IsActive,
                UpdatedAt = item.UpdatedAt
            });
        });

        // PUT /api/admin/announcement
        routes.MapPut("/api/admin/announcement", async (
            UpdateAnnouncementRequest req,
            IDbContextFactory<ApplicationDbContext> dbFactory,
            HttpContext httpContext) =>
        {
            var user = httpContext.User;
            if (!user.IsInRole("Admin") && !PdnodeVote.Data.SystemConstants.IsRootAdmin(user.FindFirst(ClaimTypes.NameIdentifier)?.Value))
            {
                return Results.Forbid();
            }

            await using var context = await dbFactory.CreateDbContextAsync();
            var item = await context.SystemAnnouncements.FirstOrDefaultAsync(a => a.Id == 1);
            if (item == null)
            {
                item = new SystemAnnouncement { Id = 1 };
                context.SystemAnnouncements.Add(item);
            }

            item.Message = req.Message?.Trim() ?? string.Empty;
            item.TargetUrl = string.IsNullOrWhiteSpace(req.TargetUrl) ? null : req.TargetUrl.Trim();
            item.IsActive = req.IsActive;
            item.UpdatedAt = DateTime.UtcNow;

            await context.SaveChangesAsync();
            return Results.Ok(new ServiceResult { Success = true, Message = "Announcement updated." });
        }).RequireAuthorization();

        // ==================== USER ACTIVITY & PROFILE ====================
        routes.MapGet("/api/user/activity/voted", async (
            PollService pollService,
            HttpContext httpContext,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 15) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var res = await pollService.GetVotedPollsByUserAsync(userId, page, pageSize);
            return Results.Ok(res);
        }).RequireAuthorization();

        routes.MapGet("/api/user/activity/commented", async (
            PollService pollService,
            HttpContext httpContext,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 15) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var res = await pollService.GetCommentedPollsByUserAsync(userId, page, pageSize);
            return Results.Ok(res);
        }).RequireAuthorization();

        routes.MapGet("/api/user/profile/{username}", async (
            string username,
            PollService pollService,
            HttpContext httpContext) =>
        {
            var currentUserId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var profile = await pollService.GetUserPublicProfileAsync(username, currentUserId);
            if (profile == null) return Results.NotFound();
            return Results.Ok(profile);
        });

        // ==================== CATEGORIES & SUBSCRIPTIONS ====================
        routes.MapGet("/api/categories", async (ICategoryService categoryService) =>
        {
            var categories = await categoryService.GetCategoryTreeAsync();
            return Results.Ok(categories);
        });

        routes.MapPost("/api/categories/{id:int}/subscribe", async (
            int id,
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var res = await pollService.ToggleCategorySubscriptionAsync(userId, id);
            return Results.Ok(res);
        }).RequireAuthorization();

        routes.MapGet("/api/categories/subscriptions", async (
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var list = await pollService.GetSubscribedCategoryIdsAsync(userId);
            return Results.Ok(list);
        }).RequireAuthorization();

        routes.MapPost("/api/category-requests", async (
            SubmitCategoryRequest request,
            ICategoryService categoryService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var result = await categoryService.SubmitCategoryRequestAsync(userId, request);
            if (!result.Success) return Results.BadRequest(result);
            return Results.Ok(result);
        }).RequireAuthorization();

        routes.MapGet("/api/tags/popular", async (ICategoryService categoryService) =>
        {
            var tags = await categoryService.GetPopularTagsAsync();
            return Results.Ok(tags);
        });

        routes.MapGet("/api/comments/gating", async (
            ICommentService commentService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var required = await commentService.RequiresCommentModerationAsync(userId);
            return Results.Ok(new CommentGatingDto { RequiresModeration = required });
        }).RequireAuthorization();

        routes.MapDelete("/api/comments/{id:int}", async (
            int id,
            ICommentService commentService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var result = await commentService.DeleteCommentAsync(id, userId);
            if (!result.Success) return Results.BadRequest(result);
            return Results.Ok(result);
        }).RequireAuthorization();

        // ==================== CONTENT REPORTING ====================
        routes.MapPost("/api/reports", async (
            SubmitReportRequest request,
            IReportService reportService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var res = await reportService.SubmitReportAsync(userId, request);
            if (!res.Success) return Results.BadRequest(res);
            return Results.Ok(res);
        }).RequireAuthorization();

        // ==================== IN-APP NOTIFICATIONS ====================
        routes.MapGet("/api/notifications", async (
            INotificationService notificationService,
            HttpContext httpContext,
            [FromQuery] int limit = 20) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var list = await notificationService.GetNotificationsAsync(userId, limit);
            return Results.Ok(list);
        }).RequireAuthorization();

        routes.MapGet("/api/notifications/unread-count", async (
            INotificationService notificationService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            var count = await notificationService.GetUnreadCountAsync(userId);
            return Results.Ok(count);
        }).RequireAuthorization();

        routes.MapPost("/api/notifications/{id:int}/read", async (
            int id,
            INotificationService notificationService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            await notificationService.MarkAsReadAsync(userId, id);
            return Results.Ok(new ServiceResult { Success = true, Message = "Marked as read." });
        }).RequireAuthorization();

        routes.MapPost("/api/notifications/read-all", async (
            INotificationService notificationService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

            await notificationService.MarkAsReadAsync(userId, null);
            return Results.Ok(new ServiceResult { Success = true, Message = "All marked as read." });
        }).RequireAuthorization();
    }

    private static string GetClientIp(HttpContext context) => ClientIpAccessor.GetClientIp(context);

    /// <summary>
    /// Builds the absolute public URL of a poll for use in generated QR codes.
    /// </summary>
    /// <remarks>
    /// The client-supplied <c>Host</c> header is attacker controlled while <c>AllowedHosts</c> is "*"
    /// (the default configuration), so reflecting it produced QR codes pointing at whatever origin the
    /// caller asked for. An operator-configured <see cref="PdnodeVote.Data.SystemConstants.PublicBaseUrlConfigKey"/>
    /// therefore wins; the request scheme/host is only a fallback for local/dev runs. Operations
    /// deploying this app publicly should always set that value.
    /// </remarks>
    public static string BuildPublicPollUrl(IConfiguration configuration, HttpContext context, int pollId)
    {
        var configured = configuration[PdnodeVote.Data.SystemConstants.PublicBaseUrlConfigKey];
        if (!string.IsNullOrWhiteSpace(configured)
            && Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            return $"{configured.Trim().TrimEnd('/')}/poll/{pollId}";
        }

        return $"{context.Request.Scheme}://{context.Request.Host}/poll/{pollId}";
    }
}
