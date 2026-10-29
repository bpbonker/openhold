# Build a numbering scheme

This builds a numbering scheme part by part, points a category (and the catalogue) at it, and shows how people get
numbers from it.

A new vault has no numbering schemes: Openhold doesn't impose prefixes, part types or number formats. You need
*Manage Configuration* (administrators have it).

## How a scheme works

A scheme is a list of **parts**, in order:

| Part | Gives | Settings |
|---|---|---|
| Fixed text | The same text every time: a prefix (`DOC`) or a separator (`-`) | The text |
| Pick list | One of your codes, e.g. `05` = Hydraulics | Codes and meanings; a default |
| Typed value | Text people type | Letters, digits or both; shortest and longest; a default |
| Code of a folder above | The code of a folder named `CODE - Name` above where the file is saved | Optionally the codes allowed; a default |
| Parent assembly's number | The number of the assembly the file is made in | None |
| Counter | 001, 002, 003… | Digits |
| Check digit | One digit that catches typing mistakes | None |
| Year | 26 or 2026 | Two or four digits |
| Site code | The office issuing the number, e.g. HQ | None |

**The counter counts separately for each different text before it.** In `PRJ01-02-001`, every project and area
starts again at 001.

## Build one

The example builds project model numbers like `PRJ01-02-001`: project, area, then a count.

1. **Tools › Administration… › Numbering.**
2. Click **Add Scheme**, or **From a Template…** to start from a ready-made one (see below).
3. Fill in **Name** (e.g. `Project models`), **Description** (shown when people ask for a number) and
   **First number** (where each counter starts, e.g. `1`, or `100000` for six-digit numbers).
4. Build the parts. A new scheme starts with a single counter. Select a part, click **Add Part…** and choose a kind;
   the new part goes after the selected one. Use **Up**, **Down** and **Remove** to arrange them. For the example:

   | # | Kind of part | Settings |
   |---|---|---|
   | 1 | Code of a folder above | Name `Project`, shown as *Project* |
   | 2 | Fixed text | `-` |
   | 3 | Code of a folder above | Name `Area`, shown as *Area of the machine*; codes `01` Frame, `02` Structure, `05` Hydraulics… |
   | 4 | Fixed text | `-` |
   | 5 | Counter | Digits `3` |

   **Numbers look like** shows the result as you build.
5. Leave **In use** ticked. Tick **Reuse numbers that were reserved but never used** only if gaps in your numbers
   matter to you.
6. Click **Save This Scheme**. Numbering schemes are saved one at a time with this button, not with
   **Save Changes**.

Folder parts are matched from the top down: saving into `$/Projects/PRJ01 - Pump skid/02 - Structure` fills Project
with `PRJ01` and Area with `02`. Folder parts side by side with nothing between them share one folder code, such as
`MR03`.

## Number the parts of an assembly

For components named after their assembly (`PRJ01-02-001-001` under `PRJ01-02-001`), build a second scheme:
**Parent assembly's number**, Fixed text `-`, **Counter** with 3 digits. Each assembly then counts its own
components from 001. The *Parts of an assembly* template is exactly this.

## Templates

**From a Template…** adds an editable copy of one of these. Nothing is used until you save it.

| Template | Numbers look like |
|---|---|
| Part numbers | `P-100000-3`: one meaningless number per part, with a check digit |
| Project models | `PRJ01-02-001`: project and area from folders, then a count |
| Parts of an assembly | `PRJ01-02-001-001`: the assembly's number, then a count |
| Family prefix | `BRK-00012`: a family code from a pick list, then a count per family |
| Documents by year | `DOC-26-0001`: counting from 1 each year |
| Numbers per site | `HQ-00001`: each office counts on its own |

## Point categories at the scheme

1. On the **Categories** tab, select a category (e.g. *Engineering*) and choose the scheme under
   **Numbering scheme**. For files made inside an assembly, choose the assembly scheme under
   **Numbering for files made inside an assembly**; leave it as *(no numbering)* to use the first scheme for these too.
2. Or do it from the **Naming Rules** tab, which shows every category's rule on one page.
3. Test it with **Try a name** at the bottom of **Naming Rules**: type a folder (e.g.
   `$/Projects/PRJ01 - Pump skid/02 - Structure`), pick a file type, optionally an assembly it's made in, and click
   **Try It**.
4. To make the rule compulsory, tick **Required** on **Naming Rules** (or **New files must be named by this scheme**
   on **Categories**). A new file whose name doesn't follow the scheme is then refused at check-in. Files already in
   the vault are never affected. Without it, the rule is a suggestion.
5. Type a note in **What changed** and click **Save Changes**.

## Number the catalogue

Catalogue items (universal part numbers) need a scheme too; until one is chosen, nobody can add items.

1. **Administration › Item Classes.**
2. Under **Catalogue items are numbered by**, choose a scheme, e.g. one made from the *Part numbers* template.
3. **Save Changes.**

A class can use its own scheme instead (**Numbering for this class's items**). See
[The catalogue](../administration.md#the-catalogue-item-master).

## Get a number

| Where | How |
|---|---|
| **Inventor** | **Openhold tab › Save to Openhold** on a new document's first save. Pick the vault folder; the fields fill in from the folders and the assembly it's made in. The number is taken only when you click **Save**, and is written to Part Number. A drawing is saved beside its model under the model's name. |
| **Openhold Explorer** | **Tools › New Number…**: choose a scheme, fill in its fields, click **Reserve Number**, then **Copy**. Save your file under that name and check it in. |
| **AutoCAD** | Numbering on first save isn't built for AutoCAD yet. Use **Tools › New Number…** in Openhold Explorer, then save the drawing under that name. |
| **Copy Design** | **Number the copies from** gives the copies the next numbers from a scheme. See [Copy, rename and move](copy-design-rename-move.md). |

A number is never issued twice, and numbers already used by files in the vault are skipped.

## Changing a scheme later

- Once a scheme has issued numbers, its parts, first number and existing codes are fixed. You can still add codes,
  reword meanings and rules, rename it, or untick **In use** to stop it issuing numbers.
- For a different structure, build a new scheme and point the category at it.
- **Delete** works only on a scheme that has never issued a number and that nothing uses.
- **Template text (advanced)** shows the scheme as one line (`{field:Project}-{field:Area}-{seq:000}`), for copying
  it to another vault: paste it there and click **Use This Text**.

## If something goes wrong

- **The scheme isn't offered on the Categories tab.** Save it with **Save This Scheme** first, and check **In use**
  is ticked.
- **A field doesn't fill in from the folder.** The folder must be named `CODE - Name` (code, space, dash, space,
  name), and the code must be one the part allows, if you listed any.
- **Check-in refuses a new file's name.** The category's rule is **Required**. Get the name from **New Number** or
  **Save to Openhold**, or untick **Required**.
