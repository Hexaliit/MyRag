using DoomSummarizer.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using SendGrid;
using SendGrid.Helpers.Mail;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Sends digest/newsletter emails via SMTP (MailKit) or SendGrid.
/// Config-driven: provider, credentials, recipients, template are all in DoomConfig.Email.
/// </summary>
public sealed class EmailService
{
    private readonly EmailConfig _config;
    private readonly ApiKeyService? _apiKeys;

    public EmailService(EmailConfig config, ApiKeyService? apiKeys = null)
    {
        _config = config;
        _apiKeys = apiKeys;
    }

    /// <summary>
    /// Send an HTML email with the rendered digest content.
    /// </summary>
    public async Task<bool> SendAsync(
        string htmlBody,
        string? subject = null,
        string? toOverride = null,
        CancellationToken ct = default)
    {
        if (!_config.Enabled)
        {
            AnsiConsole.MarkupLine("[yellow]Email delivery is disabled. Set email.enabled=true in config.[/]");
            return false;
        }

        var resolvedSubject = subject ?? _config.SubjectTemplate
            .Replace("{{DATE}}", DateTime.Now.ToString("MMMM d, yyyy"))
            .Replace("{{QUERY}}", "");

        var recipients = (toOverride ?? _config.ToAddresses)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (recipients.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No recipients configured. Set email.toAddresses in config.[/]");
            return false;
        }

        if (string.IsNullOrEmpty(_config.FromAddress))
        {
            AnsiConsole.MarkupLine("[red]No sender address configured. Set email.fromAddress in config.[/]");
            return false;
        }

        return _config.Provider.ToLowerInvariant() switch
        {
            "sendgrid" => await SendViaSendGridAsync(htmlBody, resolvedSubject, recipients, ct),
            "smtp" => await SendViaSmtpAsync(htmlBody, resolvedSubject, recipients, ct),
            _ => throw new InvalidOperationException($"Unknown email provider: {_config.Provider}. Use 'smtp' or 'sendgrid'.")
        };
    }

    private async Task<bool> SendViaSendGridAsync(
        string htmlBody, string subject, List<string> recipients, CancellationToken ct)
    {
        var apiKey = _config.SendGridApiKey;

        // Try user secrets / env vars if config key is empty
        if (string.IsNullOrEmpty(apiKey))
            apiKey = _apiKeys?.GetService("sendgrid")?.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
            apiKey = Environment.GetEnvironmentVariable("DOOM_SENDGRID")
                     ?? Environment.GetEnvironmentVariable("SENDGRID_API_KEY");

        if (string.IsNullOrEmpty(apiKey))
        {
            AnsiConsole.MarkupLine("[red]SendGrid API key not configured. Set via:[/]");
            AnsiConsole.MarkupLine("[grey]  dotnet user-secrets set \"SendGrid\" \"SG.xxx\"[/]");
            AnsiConsole.MarkupLine("[grey]  or env var DOOM_SENDGRID / SENDGRID_API_KEY[/]");
            return false;
        }

        var client = new SendGridClient(apiKey);
        var from = new EmailAddress(_config.FromAddress, _config.FromName);
        var msg = new SendGridMessage
        {
            From = from,
            Subject = subject,
            HtmlContent = htmlBody
        };

        foreach (var to in recipients)
            msg.AddTo(new EmailAddress(to));

        var response = await client.SendEmailAsync(msg, ct);
        var statusCode = (int)response.StatusCode;

        if (statusCode is >= 200 and < 300)
        {
            AnsiConsole.MarkupLine($"[green]Email sent via SendGrid to {recipients.Count} recipient(s)[/]");
            return true;
        }

        var body = await response.Body.ReadAsStringAsync(ct);
        AnsiConsole.MarkupLine($"[red]SendGrid error ({statusCode}): {body}[/]");
        return false;
    }

    private async Task<bool> SendViaSmtpAsync(
        string htmlBody, string subject, List<string> recipients, CancellationToken ct)
    {
        var smtp = _config.Smtp;

        // Resolve credentials from user secrets / env vars
        var username = smtp.Username;
        var password = smtp.Password;

        if (string.IsNullOrEmpty(username))
            username = Environment.GetEnvironmentVariable("DOOM_SMTP_USER");
        if (string.IsNullOrEmpty(password))
            password = _apiKeys?.GetService("smtp_password")?.ApiKey
                       ?? Environment.GetEnvironmentVariable("DOOM_SMTP_PASSWORD");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_config.FromName, _config.FromAddress));
        foreach (var to in recipients)
            message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;

        message.Body = new TextPart("html") { Text = htmlBody };

        try
        {
            using var client = new SmtpClient();
            var secureSocket = smtp.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
            await client.ConnectAsync(smtp.Host, smtp.Port, secureSocket, ct);

            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
                await client.AuthenticateAsync(username, password, ct);

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            AnsiConsole.MarkupLine($"[green]Email sent via SMTP ({smtp.Host}) to {recipients.Count} recipient(s)[/]");
            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]SMTP error: {ex.Message}[/]");
            return false;
        }
    }
}
