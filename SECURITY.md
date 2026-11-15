# Security

Rhythm of Life holds a household's private data, so security reports matter here. Thank you for
taking the time.

## Reporting a problem

**Please do not open a public issue for a security problem.**

Report it privately through this repository's **Security** tab: choose **Report a vulnerability**.
Only the maintainers can read what you send there.

It helps to include:

- what you found and why it matters (what someone could read, change or break);
- the steps to reproduce it, on fictional data;
- the version or commit, and how the install is exposed (this computer only, home network,
  Tailscale, Cloudflare, a reverse proxy).

Never include real personal or health data, real tokens or real host names in a report.

## What to expect

This is a small project run by volunteers. We will acknowledge a report as soon as we can, tell you
whether we can reproduce it, and credit you in the release notes if you want. There is no bug bounty.

Please give us a reasonable time to fix a problem before you write about it in public.

## Which versions get fixes

Only the latest release. The project is in alpha: there are no long-term support versions.

## What is and is not a vulnerability here

Read [docs/security.md](docs/security.md) first. It describes the model, and some things that look
like holes are the design:

- **There is no login inside Rhythm of Life.** Whoever can reach the app sees everything in the
  install. Who can reach it is decided by where it listens and what stands in front of it.
- **The household shares everything.** Everyone listed in an install sees all of its data.

Things we do want to hear about include: reaching an install's data without a token or from outside
the network it listens on, a token doing more than its role allows, a way for content (a note, a
document, a web page, a memory) to make an AI client or the app act against the household, secrets
appearing in logs or output, and anything that lets one install's commands touch another's data.

[docs/known-limits.md](docs/known-limits.md) lists weaknesses we already know about. The code has had
no outside security review.
