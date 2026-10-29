# Connect an ERP

This sends released catalogue items (parts with their universal part numbers) to your ERP, either as a CSV file any
system can import or straight into simPRO.

ERP connections are optional. With none, Openhold sends nothing anywhere. You need *Manage Configuration*
(administrators have it), and the catalogue must already have a numbering scheme
([Number the catalogue](numbering-scheme.md#number-the-catalogue)).

## What gets sent, and when

- **What:** catalogue items once a part librarian has **released** them, and every later change to them, including
  making them obsolete. Drafts are never sent. Files (models, drawings) are not sent.
- **When:** the change is queued in the same step as the item change itself, and sent within about a minute.
- **If the ERP is down:** sending is retried with growing gaps, up to six hours apart. Nothing is lost.
- **Where to see it:** each item in **Tools › Catalogue…** shows, at the bottom, where it stands in each ERP.
  Failures are also in the server log.

## Connect a CSV file

The CSV connector keeps one file with a row per part number, for any ERP or spreadsheet to import. Columns:
*Part number, Description, Unit, Kind, Class, State, Manufacturer, Manufacturer part number, Other numbers, Updated*.
Each write replaces the file in one step, so an importer never sees half a file.

1. **Tools › Administration… › ERP › New Connection.**
2. **Kind:** *CSV file*.
3. **Name:** what people see, e.g. `ERP import (Company A)`. **Id:** a short id that never changes, e.g.
   `company-a-csv`.
4. **File:** where to keep the file, as the server sees it:
   - Windows server: e.g. `\\erp\imports\openhold-items.csv`. The server's service account needs write access there.
   - Docker server: a path inside the container, e.g. `/data/erp/openhold-items.csv` (in the `openhold_data`
     volume).
5. Tick **Switched on: send released items to this ERP** and click **Save Connection**.
6. Click **Test**. It checks the server can write in that folder.
7. Click **Send All Released Items** to queue everything already released (and obsolete).

Then point your ERP's import at the file.

## Connect simPRO

1. **In simPRO**, create a top-level catalogue group for Openhold's items, e.g. *Openhold Parts* (**Setup › Materials
   › Catalogue groups**), and an API key (**Setup › System › API keys**). Or use an OAuth client ID and secret.
2. **Tools › Administration… › ERP › New Connection**, **Kind:** *simPRO*.
3. Fill in:

   | Field | Example |
   |---|---|
   | **Name**, **Id** | `simPRO (Company A)`, `company-a-simpro` |
   | **simPRO address** | `https://yourcompany.simprosuite.com` (must be https) |
   | **Company ID** | `0` on a single-company build |
   | **Catalogue group** | `Openhold Parts` |
   | **API key** | The key from simPRO; or leave it empty and fill in **Client ID** and **Client secret** |

4. Tick **Switched on: send released items to this ERP** and click **Save Connection**. Secrets such as the API key
   are stored encrypted and never shown again; type a new one to replace it.
5. Click **Test**. It signs in, reads the catalogue and finds the group, without changing anything in simPRO.
6. Click **Send All Released Items**.

### What the simPRO connector never does

- It creates items **only in its own catalogue group**.
- It **never changes items it didn't create.** If simPRO already has an item with the same part number, Openhold
  reports it and leaves it alone. Before every update it checks the item is still in its group.
- On updates it changes only the **name**, **manufacturer** and **archived** flag. Notes, prices and suppliers you
  keep in simPRO are left as they are. Making an item obsolete in Openhold archives it in simPRO.
- It never deletes anything in simPRO.

## Switching off or removing a connection

- Untick **Switched on** and **Save Connection** to stop sending; the connection and what it sent are kept.
- **Remove** stops sending to it. What was already sent stays in the ERP.

## Connections in the server's settings file

On a Windows server, connections can also be set in `appsettings.Production.json` under `Openhold:ErpConnections`,
with secrets in the service's environment (e.g. `Openhold__ErpConnections__0__Settings__ApiKey`). These show in
Administration but can only be changed in the file. See the
[administrator guide](../administration.md#erp-connections).

## If something goes wrong

- **Test fails with "no top-level catalogue group called …".** Create the group in simPRO, at the top level, with
  exactly that name.
- **An item shows as failed: "simPRO already has an item with part number …".** An item with that part number
  existed in simPRO before. Openhold won't touch it: remove the old one in simPRO, or give the Openhold item another
  number.
- **"… is no longer in the … group, so Openhold left it alone."** Someone moved the item in simPRO. Move it back to
  Openhold's group.
- **CSV: "Couldn't write …".** The server's account can't write to that folder, or the share is unreachable.
- **Nothing is sent.** Check the connection is **Switched on**, and that the items are released, not *Draft*.

Other ERPs plug in through the same interface (`IErpConnector`); only CSV and simPRO are built today.
