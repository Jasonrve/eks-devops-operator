# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Use GitHub's private vulnerability reporting
(Security tab → Report a vulnerability) on this repository.

Include what you found, how to reproduce it, and the impact. You will get an acknowledgement within a few working days.

## Scope

In scope: ways for a namespace user to obtain AWS access outside the configured guardrails, to modify or delete IAM resources the operator does not own,
to influence which role the operator assumes, or to extract credentials from logs, status or events.

See `docs/guide/security.md` for the threat model and known residual risks.
