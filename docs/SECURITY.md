# Openhold security

Openhold holds an organisation's engineering intellectual property: designs, drawings, bills of materials and their
history. Security is a design requirement. This document covers:
- the threats Openhold is designed against;
- each control, mapped to the **SOC 2 Trust Services Criteria** (and to ISO 27001 and the ACSC Essential Eight),
  with where it's implemented and where its evidence comes from;
- what each organisation running Openhold must do itself.

Openhold supports an organisation's compliance. It can't make an organisation compliant on its own. SOC 2 and ISO
27001 cover how the whole organisation runs systems, including policies, people and IT operations. Openhold
provides the application-level controls and the evidence an auditor asks for.

## Threats designed against

| Threat | Example | Primary controls |
|---|---|---|
| Unauthorised access to IP | Someone on the network browses or downloads drawings | Sign-in on every request; groups and privileges; folder permissions; TLS |
| Stolen or guessed passwords | A partner's password is phished | Two-factor codes; lockout; rate limiting; Windows sign-in where possible |
| Insider misuse | A leaver keeps access, or bulk-downloads the vault | Disabling stops every sign-in method within 30 seconds; access reviews; audit trail |
| Tampering and rewriting history | Someone hides who changed a released drawing | Immutable versions; append-only audit enforced by the database; content hashes |
| Ransomware and data loss | A server is encrypted or destroyed | Encrypted backups kept off the server; immutable file chunks; a rehearsed restore |
| Interception | Traffic between offices or from home is captured | HTTPS everywhere; HSTS; short-lived tokens |
| Token theft | A copied session token is replayed | 15-minute access tokens; rotating session tokens with reuse detection; sessions saved encrypted per Windows user |
| Compromised dependency or add-in | A malicious package is loaded into AutoCAD or Inventor | Dependency vulnerability scan and SBOM before every release; add-ins from trusted locations |
| Misconfiguration | A development setting reaches production | The server refuses to start insecurely (fail-closed checks) |

## Controls, mapped to SOC 2

**Common Criteria (CC), Availability (A1) and Confidentiality (C1).** "Org" marks what the organisation running
Openhold must provide.

| SOC 2 | Control in Openhold | Where | Evidence |
|---|---|---|---|
| CC6.1 Logical access | Every API request needs a signed-in identity: Windows (Kerberos/NTLM), an Openhold account, or an organisation identity provider (OpenID Connect). Only `/health`, the sign-in options, sign-in itself, the vault list and the QR check page are anonymous. | `Identity/AuthenticationSetup.cs`, `Program.cs` | `AccountTests`, `SecurityTests`; a request without sign-in gets 401 |
| CC6.1 | Least privilege: rights beyond everyday work are privileges granted to groups (Manage Configuration, Manage Users, Change Category, Delete Released Files, Part Librarian, Run Jobs, View Audit). Nobody has them by default. | `Configuration/VaultConfiguration.cs` | Access review |
| CC6.1 | Openhold-account passwords: PBKDF2-SHA512, 210,000 rounds, random salt; at least 12 characters, not containing the username; temporary passwords must be changed at first sign-in. | `Identity/Passwords.cs` | `AccountTests` |
| CC6.1 | Two-factor sign-in (TOTP authenticator apps), requirable per account; administrators reset it for a new phone. Windows and organisation accounts use their own MFA policy (Org). | `Identity/Totp.cs`, `AccountService.cs` | `AccountTests`; access review shows two-factor status |
| CC6.1 | Lockout after 5 wrong passwords or codes (15 minutes); 20 sign-in attempts per minute per address; one error message for wrong user and wrong password, so accounts can't be probed. | `AccountService.cs`, `Program.cs` | Audit `sign_in_failed`, `account_locked` |
| CC6.1 | Sign-in: access tokens are RS256-signed and last 15 minutes; session tokens are random, stored only as hashes, rotated on every use, and reuse ends all of that account's sessions. The signing key is readable only by the service account. | `AccountService.cs`, `SigningKeys.cs` | `AccountTests` |
| CC6.1/CC6.3 | Folder permissions: per group, None (hidden), Read or Change on a folder and everything below it. The deepest folder with a rule for the person's groups decides, and the most generous of its rules applies. Hidden files answer "not found" everywhere (listings, search, where-used, path lookups, the trash), so their existence isn't revealed. "Explain why" shows an administrator which rule decides a person's access. | `VaultConfiguration.AccessTo`, `Browsing/FolderGuard.cs` | `FolderPermissionTests` |
| CC6.1 | Authorised chunk access: once any folder is restricted, file content (chunks) is only served to people who were given the file's manifest through a file they may read. | `FolderGuard.RequireChunksAsync` | `FolderPermissionTests` |
| CC6.2 Provisioning | Openhold accounts are created by people with Manage Users; Windows and organisation accounts are recorded at first sign-in and can be disabled like any other. Usernames keep their domain unless it's a home domain, so people from different organisations never share an account. | `AccountService.cs`, `Actor.cs` | Audit `account_created`, `account_recorded` |
| CC6.2/CC6.3 Removal and review | Disabling an account stops every sign-in method on every server within 30 seconds and ends its sessions. Access review lists everyone, how they sign in, last sign-in, groups and privileges, including group members who never signed in. | `AccountService.cs`, `Audit/AuditService.cs` | Access review report (Org: review quarterly) |
| CC6.6 Boundary protection | HTTPS (certificate from the machine store) with HSTS; firewall rule for the HTTPS port only; security headers (`nosniff`, `DENY` framing, `no-referrer`, `no-store`). | `Program.cs`, `deploy/appsettings.Production.example.json`, `tools/install-server.ps1` | Org: certificate and network configuration |
| CC6.7 Data in transit | TLS for all traffic; access tokens never logged. | `Program.cs` | TLS scan (Org) |
| CC6.8 Malicious software | Dependency vulnerability scan and SBOM before every release; add-ins load from trusted locations without lowering AutoCAD's SECURELOAD. | `tools/security-check.ps1` | `data/sbom.json`, scan output |
| CC7.2 Monitoring | Append-only audit of check-outs, check-ins, state changes, approvals, deletions, configuration saves, sign-ins (and failures), lockouts and account changes; daily server log files, kept 90 days by default; warnings and errors also go to the Windows event log. | `audit_events` (database triggers reject change), `Logging/FileLogging.cs` | Audit log export (View Audit); log files |
| CC7.3/CC7.4 Incident response | Disable an account, sign it out everywhere, reset password and two-factor; rotate the token-signing key; restore from backup. | Administration > Users; procedures below | Audit trail of the response |
| CC8.1 Change management | Configuration is versioned: every save is kept with who saved it, when and why, and can be rolled back. Code changes are reviewed and tested before release; the server upgrades its database itself. | `vault_config`, Administration > History | Configuration history; release notes (Org: change approval) |
| A1.2 Backup and recovery | `openhold-admin` backs up each database, the file store (incrementally), settings, the signing key and logs, encrypted with AES-256-GCM under a passphrase (stored for the scheduled task in a file only administrators can read, with a copy kept off the server); `verify` checks backups; `restore` rebuilds into a new database and refuses one with data. | `src/Openhold.Admin`, `tools/schedule-backup.ps1` | `last-backup.json`; restore rehearsal record (Org: test at least twice a year) |
| A1.2 | The service restarts itself after failures; the health endpoint shows it's up. | `tools/install-server.ps1` | Monitoring (Org) |
| C1.1 Confidentiality | Files are only served to signed-in users; data folders and settings are readable only by the service account and administrators; backups are encrypted; sessions on PCs are encrypted per Windows user (DPAPI). | `install-server.ps1`, `SessionStore` | File-system permissions (Org: disk encryption with BitLocker) |
| PI1 Processing integrity | Every file chunk is checked against its BLAKE3 hash when stored, read, uploaded and downloaded; versions are immutable; check-ins are atomic; lifecycle rules (released parts, approvals by people other than the requester) are enforced on the server. | `Content/*`, `CheckInService.cs`, `LifecycleService.cs` | Test suite |

**ACSC Essential Eight:** MFA (two-factor for Openhold accounts; inherited for Windows and organisation accounts),
restricting administrative privileges (privileges per group, audited), regular backups (encrypted, off-server,
tested), patching applications (dependency scan each release), application control (add-ins from trusted
locations).

**ISO/IEC 27001:2022 Annex A:** these map to the same controls:
- A.5.15–5.18, access control and identity;
- A.8.2, privileged access;
- A.8.5, secure authentication;
- A.8.13, backup;
- A.8.15–8.16, logging and monitoring;
- A.8.24, cryptography;
- A.8.25–8.29, secure development and testing;
- A.8.32, change management.

## Procedures

- **Access review (quarterly).** Administration > Users and the access review (View Audit). Disable anyone who has
  left, and remove privileges people no longer need. Keep the export as evidence.
- **Someone leaves.** Disable the account (Administration > Users). For Windows accounts, also disable them in
  Active Directory. The audit log shows what they did.
- **Suspected stolen password or token.**
  1. Disable the account, or reset its password and two-factor.
  2. Sign it out everywhere.
  3. Read its recent audit entries (View Audit) to see what was done.
- **Rotating the token-signing key** (yearly, or if the server may have been compromised):
  1. Stop the service.
  2. Move `token-signing-key.pem` aside.
  3. Start the service, which creates a new key.
  4. Mark the old key retired in `signing_keys` (`UPDATE signing_keys SET retired_at = now() WHERE kid = '<old>'`).
  5. Everyone signs in again within 15 minutes.
- **Backups.**
  - Nightly via `tools/schedule-backup.ps1` to a destination off the server.
  - `openhold-admin verify` weekly.
  - Rehearse a full restore into a spare database at least twice a year, and record the result.
  - Keep the passphrase in the organisation's password manager, not on the server.
- **Before every release.** Run `tools/security-check.ps1` (vulnerable dependencies, SBOM, secrets) and the full
  test suite. Both must pass.

## Still to build

| Control | Why |
|---|---|
| Stop `/chunks/missing` revealing whether a chunk exists | Minor information leak: it says yes or no, never returns content. |
| Mutual TLS between site servers and the authority | Multi-site deployments. |
| Signed CAD add-ins and installers (Authenticode) | Application control on engineering PCs. |
| Alerts (failed jobs, lockouts, unusual download volumes, backup failures) by email or Teams | Faster detection. |
| Independent penetration test before a wide rollout | External assurance. |

## Reporting a vulnerability

Report security problems privately, as described in the [security policy](../.github/SECURITY.md). Don't open
a public issue. You'll get an acknowledgement within 5 working days.
