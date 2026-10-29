# Security policy

Openhold stores organisations' engineering data. Please report security problems privately, as described below.

## Reporting a vulnerability

**Please don't open a public issue for a security problem.** Report it privately through GitHub:
**Security › Report a vulnerability** on this repository. Include:
- what an attacker could do, and what they need first (network access, an account, a role);
- the steps to reproduce it, and the Openhold version or commit;
- any logs or proof of concept, with no real company data in them.

You'll get an acknowledgement within 5 working days. Once the problem is confirmed, a fix is prepared privately
and released, and the advisory is published with credit to you unless you'd rather stay anonymous.

## Supported versions

Until 1.0 is released, only the latest commit on the main branch gets security fixes. After 1.0, the latest
minor release does.

## How Openhold is secured

[docs/SECURITY.md](../docs/SECURITY.md) describes the threat model, the security controls (mapped to SOC 2,
ISO 27001 and the ACSC Essential Eight) and what each organisation running Openhold must do itself.
