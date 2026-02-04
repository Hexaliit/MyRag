# Automation (JSON + file outputs + scheduling)

DoomSummarizer is console-first, but it’s designed to be automatable via:

- JSON output (`--json` / `-t json`)
- File exports (`-o/--output` + `-t/--template`)
- External schedulers (cron / Windows Task Scheduler)

## JSON output

Use JSON for downstream scripts, other agents, or post-processing:

```bash
doomsummarizer scroll "ai security news" --json > digest.json
doomsummarizer scroll "ai security news" -t json -o digest.json
```

## File export (Markdown/HTML)

Examples:

```bash
doomsummarizer scroll "security updates" -t file -o digest.md
doomsummarizer scroll "security updates" -t email -o email.html
doomsummarizer scroll "security updates" -t newsletter -o newsletter.html
```

If the output path ends in:

- `.md` / `.txt`: written as text/markdown
- `.html`: written as HTML (use `email`/`newsletter` templates for ready-to-send markup)
- `.json`: written as JSON (use `--json` or `-t json`)

## Scheduling patterns (today)

DoomSummarizer does not yet include a built-in scheduler, but you can schedule it externally.

### Windows Task Scheduler

Create a task that runs something like:

```text
doomsummarizer scroll "weekly engineering digest" -t newsletter -o C:\path\digest.html
```

Then have your email/send step run separately (PowerShell, SMTP tool, Outlook automation, etc.).

### cron (Linux/macOS)

```bash
0 8 * * 1 doomsummarizer scroll "weekly engineering digest" -t newsletter -o "$HOME/digest.html"
```

## Roadmap (future)

The direction is:

- First-class scheduled runs (built-in cron-like schedules)
- Templated email delivery (SMTP/SendGrid/etc.)
- Templated Slack/Teams posting

Until then, `--json` + templates are the “automation surface area”.

