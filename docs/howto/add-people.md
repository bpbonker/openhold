# Add people

This gives people access to Openhold, puts them in the right groups, limits folders where you need to, and shows
how to lock someone out when they leave.

Account changes need the *Manage Users* privilege; groups and folder permissions need *Manage Configuration*.
Administrators have both. Everything here is in **Tools › Administration…**.

## 1. Choose how people sign in

| Sign-in | Use it for | Available on |
|---|---|---|
| **Windows account** | Staff on your Windows domain. Nothing to type. | Windows Server installs |
| **Openhold account** | Everyone on a Docker install; partners, contractors and offices on another domain | All servers |
| **Organisation account** (OpenID Connect, e.g. Microsoft Entra ID) | People who sign in with their own organisation's account | Not yet usable from the PCs: see below |

Each PC chooses its sign-in method in Openhold Explorer's **Settings** ([Client PCs](../install/clients.md)).

**Organisation accounts.** The server can be told to accept an organisation's sign-in (the
`Openhold:Authentication:OpenIdConnect` settings, see the [administrator guide](../administration.md#sign-in-and-accounts)),
but Openhold Explorer and the CAD add-ins can't sign in that way yet: their **Settings** offer only Windows and
Openhold account. Until they can, give people outside your domain Openhold accounts.

## 2. Add someone with a Windows account

There's nothing to create. A Windows account appears on the **Users** tab the first time the person signs in.

- To set up their access before then, type their username into a group's **Members** (step 4).
- People on your own domains are known by their plain username (e.g. `jsmith`), because of the server's
  `HomeDomains` setting. People from other domains keep their domain in the username.

## 3. Add someone with an Openhold account

1. **Administration › Users › New Openhold Account…**
2. Fill in **Username** (e.g. `jsmith`, or `jsmith@partner.com` for someone at another company), **Full name** and,
   optionally, **Email**.
3. Leave **Require two-factor sign-in (recommended)** ticked.
4. Click **Create**. Openhold shows a temporary password once and copies it to the clipboard. Give it to the person
   privately, not by email.
5. The person sets **Settings › Sign-in** to *Openhold account* and signs in. They choose their own password (at
   least 12 characters, not containing their username), then scan the QR code with an authenticator app such as
   Microsoft Authenticator or Google Authenticator and enter the six-digit code.

Account changes on the **Users** tab take effect straight away; there's nothing to save.

## 4. Put people in groups

Groups decide who may make lifecycle changes, who approves, and who has privileges.

1. **Administration › Groups.** Select a group, or click **Add Group** and name it (e.g. `PRJ02 team`).
2. Add members: select people under **People who use Openhold** and click **Add to Group**, or type usernames into
   **Members**, one per line.
3. Tick any **Privileges** the group needs. Nobody has a privilege unless a group gives it; members of
   *Administrators* have them all.
4. Type a note in **What changed** and click **Save Changes**.

The groups a new vault starts with:

| Group | For | Privilege |
|---|---|---|
| Administrators | Openhold administrators | All of them |
| Checkers | People who approve releases on the *Reviewed release* lifecycle | None |
| Part librarians | People who release and obsolete catalogue items | Part Librarian |
| Job workers | The job worker's account ([Publish PDFs](publish-pdfs.md)) | Run Jobs |
| Site servers | The servers' replication account ([Add an office](add-an-office.md)) | Copy file contents between sites |

The other privileges (*Manage Configuration*, *Manage Users*, *Change Category*, *Delete Released Files*,
*View Audit*) are described in the [administrator guide](../administration.md#groups-privileges-and-permissions).

## 5. Limit folders (optional)

By default everyone can change everything. To restrict a folder:

1. **Administration › Permissions.**
2. Choose what applies to **Folders no rule covers**: *Change (everyone)*, *Read only* or *Hidden*.
3. Click **Add Rule…**, pick the folder, then set its **Group** and **Access**:
   - *Change*: check out, check in, rename, delete, change state;
   - *Read only*: see and get files;
   - *Hidden*: the folder and its files can't be seen anywhere, including search, Where Used and the Trash.
4. Check the result with **Explain why**: choose a **Person**, type a **Folder** (e.g. `$/Projects/PRJ02`) and click
   **Explain**. It shows what they may do and which rule decides, including changes you haven't saved yet.
5. Click **Save Changes**.

How rules combine: the deepest folder with a rule for one of the person's groups decides; if several of their groups
have rules there, the most generous wins. Administrators can always do everything.

Example: to keep `$/Projects/PRJ02` to one team, add two rules on that folder: *Everyone* › *Hidden*, and
*PRJ02 team* › *Change*.

## 6. Two-factor sign-in

For Openhold accounts. Windows and organisation accounts follow your organisation's own MFA policy.

- **Require it:** select the account on the **Users** tab and click **Require Two-Factor**. They set it up at their
  next sign-in. Click it again to stop requiring it.
- **New phone:** **Reset Two-Factor**. The person is signed out and sets it up again.
- **Turn it on yourself:** **Tools › My Account… › Set Up Two-Factor Sign-In**, scan the code, enter the six-digit
  code, **Turn On**. **My Account** is also where people change their password.

## 7. When someone leaves

1. **Administration › Users**, select the account, click **Disable or Enable** and confirm. Every sign-in method
   stops on every server within 30 seconds, and their sessions end.
2. For a Windows account, also disable it in Active Directory.
3. Remove them from their groups on the **Groups** tab, and **Save Changes**.

There's no delete: a disabled account stays, so the history of who changed what stays complete. **Disable or Enable**
again brings someone back.

Other buttons on the **Users** tab:

| Button | Does |
|---|---|
| **Reset Password** | Signs them out and shows a new temporary password |
| **Unlock** | Ends the 15-minute lock after five wrong passwords or codes |
| **Sign Out Everywhere** | Ends their sessions on every PC, e.g. after a lost laptop |

Review access every quarter: **Administration › Audit › Export Access Review…** (needs *View Audit*) saves everyone's
sign-in method, last sign-in, groups and privileges as a CSV. See [SECURITY.md](../SECURITY.md#procedures).

## If something goes wrong

- **There's no Users tab.** You need the *Manage Users* privilege.
- **Locked out after wrong passwords or codes.** Wait 15 minutes, or an administrator clicks **Unlock**.
- **Someone can't see a folder or can't check out.** Use **Explain why** on the **Permissions** tab with their
  username and the folder.
- **A Windows user isn't on the Users tab.** They haven't signed in yet. You can still add their username to groups.
